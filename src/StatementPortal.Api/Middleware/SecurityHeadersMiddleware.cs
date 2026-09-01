namespace StatementPortal.Api.Middleware;

/// <summary>
/// Baseline OWASP secure-headers hardening for every response. None of this
/// substitutes for the auth/authz checks elsewhere — it reduces the blast
/// radius of classes of bugs (clickjacking, MIME sniffing, referrer leakage,
/// accidental caching of sensitive statement/audit data) that those checks
/// don't address.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers.Remove("Server");
            headers.Remove("X-Powered-By");

            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";

            // This is an API surface for a browser-based portal, not a page the
            // browser should cache — statements and audit data are sensitive.
            headers["Cache-Control"] = "no-store";

            return Task.CompletedTask;
        });

        await _next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}
