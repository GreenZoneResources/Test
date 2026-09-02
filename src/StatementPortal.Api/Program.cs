using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
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
builder.Services.AddStatementPortalInfrastructure(builder.Configuration);

// This service is a pure resource server (see AuthenticationExtensions): it
// validates SSO-issued bearer tokens locally and never issues its own
// session, so there's no cookie/ticket-store/DataProtection machinery here —
// that responsibility belongs entirely to SSO.
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
        .AllowCredentials() // the SSO-issued "access-token" cookie is sent this way when present.
        .AllowAnyHeader()
        .WithMethods("GET", "POST"));
});

// Blunts scripted abuse of the statement-generation endpoints independent of
// anything WAF/gateway does.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("statements", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Statement Generation Portal API", Version = "v1" });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste a bearer token issued by LotusBank SSO (without the \"Bearer \" prefix)."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseSecurityHeaders();
app.UseCustomExceptionHandling();
app.UseSerilogRequestLogging(); // every request's final status code (including 401/403) lands in Seq here.

// Available in every environment, not just Development — this is an internal
// API behind CORS/rate limiting already, and the team wants it reachable for
// testing against non-local deployments too.
app.UseSwagger();
app.UseSwaggerUI(options => options.DocumentTitle = "Statement Generation Portal API");

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors("PortalFrontend");
app.UseAuthentication();

// Turns the validated token's "appRoles" claim into this app's own permission
// claims (filtered to this application only) — see AppRolesClaimsMiddleware.
// Must run after UseAuthentication (needs context.User already populated) and
// before UseAuthorization (policies read the claims this adds).
app.UseMiddleware<AppRolesClaimsMiddleware>();

app.UseAuthorization();

// Rate limiter runs after auth: the "statements" policy partitions by staff id
// from the authenticated principal, which only exists once auth has run.
app.UseRateLimiter();

app.MapControllers();

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
