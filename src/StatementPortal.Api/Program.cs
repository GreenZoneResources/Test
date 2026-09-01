using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Scalar.AspNetCore;
using Serilog;
using StatementPortal.Api.Authorization;
using StatementPortal.Api.Middleware;
using StatementPortal.Infrastructure;
using StatementPortal.Infrastructure.Auth;

// Stage 1: bootstrap logger. A throwaway LoggerConfiguration instance used only
// until the host is far enough along to build the real one below — see Stage 2.
// Do not reuse this instance, and do not add a second UseSerilog/AddSerilog call
// anywhere else in the solution: Serilog's ReloadableLogger can only be frozen
// (finalized) once per process, and a second registration throws
// "The logger is already frozen."
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithEnvironmentName()
    .WriteTo.Console()
    .WriteTo.Seq(
        context.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341",
        apiKey: context.Configuration["Seq:ApiKey"]));

builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

// Only trust forwarded headers from the known reverse proxy — accepting them
// unconditionally lets a client spoof its own IP address, which would poison
// the IP address captured in every audit record (US-10).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Clear();
    foreach (var proxy in builder.Configuration.GetSection("KnownProxies").Get<string[]>() ?? [])
        options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddStatementPortalInfrastructure(builder.Configuration);

// Server-side session store backing the cookie (US-02, US-18) — Redis so
// revocation/expiry is consistent across every portal instance behind the LB.
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("Redis");
    options.InstanceName = "StatementPortal:";
});
builder.Services.AddSingleton<Microsoft.AspNetCore.Authentication.Cookies.ITicketStore, DistributedCacheTicketStore>();

builder.Services.AddDataProtection()
    .SetApplicationName("StatementPortal")
    .PersistKeysToStackExchangeRedis(
        StackExchange.Redis.ConnectionMultiplexer.Connect(
            builder.Configuration.GetConnectionString("Redis")!),
        "StatementPortal-DataProtection-Keys");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-StatementPortal.Session";
        options.Cookie.HttpOnly = true; // US-02: never exposed to client-side JavaScript.
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;

        options.Events.OnRedirectToLogin = context =>
        {
            // This is an API — never issue an HTML redirect to unauthenticated
            // API callers (US-02 "unauthenticated request" / US-17).
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

// Wires the already-registered ITicketStore singleton into the cookie handler's
// options via DI's own options post-configuration — deliberately NOT via a
// second builder.Services.BuildServiceProvider() call, which would spin up a
// throwaway second container just to fetch one service (the same class of
// double-initialization bug behind the "logger already frozen" issue).
builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<Microsoft.AspNetCore.Authentication.Cookies.ITicketStore>(
        (options, store) => options.SessionStore = store);

builder.Services.AddAuthorization(options =>
{
    // US-17: deny-by-default; every endpoint needs [AllowAnonymous] explicitly to opt out.
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    options.AddPortalPolicies();
});

builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("PortalFrontend", policy => policy
        .WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [])
        .AllowCredentials() // required so the browser sends the session cookie cross-origin to the SPA host.
        .AllowAnyHeader()
        .WithMethods("GET", "POST"));
});

// Blunts brute-force/credential-stuffing against auth, and abuse of the
// statement-generation endpoints, independent of anything WAF/gateway does.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));

    options.AddPolicy("statements", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(PortalClaimTypes.StaffId)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseSecurityHeaders();
app.UseCustomExceptionHandling();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(opt =>
    {
        opt.Title = "Statement Generation Portal API";
        opt.Authentication = new ScalarAuthenticationOptions { PreferredSecurityScheme = "Cookie" };
    });
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors("PortalFrontend");
app.UseAuthentication();
app.UseAuthorization();
// Rate limiter runs after auth: the "statements" policy partitions by staff id
// from the authenticated principal, which only exists once auth has run.
app.UseRateLimiter();

// Per-controller [EnableRateLimiting("auth"|"statements")] attributes apply the
// policies above — MapGroup only groups endpoints registered within its own
// builder, so it cannot retroactively rate-limit MVC controllers mapped here.
app.MapControllers();

app.Run();
