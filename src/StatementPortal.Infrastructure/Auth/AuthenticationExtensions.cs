using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace StatementPortal.Infrastructure.Auth;

/// <summary>
/// This service is a pure resource server: it validates bearer tokens SSO
/// already issued and never itself authenticates a user (no login/callback
/// endpoints, no session cookie of its own, no outbound call to SSO at
/// request time). Every failure path below logs through ILogger — not
/// Console.WriteLine — specifically so a 401/403 on any endpoint is visible
/// in Seq with the path and reason, without needing to reproduce it locally.
/// </summary>
public static class AuthenticationExtensions
{
    public static IServiceCollection AddStatementPortalAuthentication(this IServiceCollection services)
    {
        services.AddSingleton<IJwtSigningKeyProvider, JwtSigningKeyProvider>();
        services.AddSingleton<AppRolesClaimsMiddleware>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = async context =>
                    {
                        // TokenValidationParameters need the DB-loaded signing key,
                        // which isn't available synchronously at options-configure
                        // time — resolved async here instead (cached after first
                        // call, see JwtSigningKeyProvider).
                        var keyProvider = context.HttpContext.RequestServices
                            .GetRequiredService<IJwtSigningKeyProvider>();
                        context.Options.TokenValidationParameters =
                            await keyProvider.GetTokenValidationParametersAsync(context.HttpContext.RequestAborted);

                        var cookieToken = context.Request.Cookies["access-token"];
                        if (!string.IsNullOrEmpty(cookieToken))
                            context.Token = cookieToken;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILogger<JwtBearerEvents>>();
                        logger.LogWarning(context.Exception,
                            "JWT authentication failed for {Path}.", context.HttpContext.Request.Path);
                        return Task.CompletedTask;
                    },
                    OnChallenge = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILogger<JwtBearerEvents>>();
                        logger.LogWarning(
                            "Unauthenticated request rejected for {Path}: {Error}.",
                            context.HttpContext.Request.Path, context.ErrorDescription ?? context.Error ?? "no token");
                        return Task.CompletedTask;
                    },
                    OnForbidden = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILogger<JwtBearerEvents>>();
                        logger.LogWarning(
                            "Authenticated request forbidden for {Path} (staff {StaffId}).",
                            context.HttpContext.Request.Path,
                            context.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);
                        return Task.CompletedTask;
                    }
                };
            });

        return services;
    }
}
