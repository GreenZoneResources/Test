using System.Net;

namespace StatementPortal.Api.Middleware;

/// <summary>
/// Converts unhandled exceptions into a generic problem response. Deliberately
/// never reflects exception messages/stack traces back to the client — those
/// go to the log only — since this API sits in front of a bank's statement and
/// audit data and a verbose error is itself an information-disclosure risk.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing {Method} {Path}",
                context.Request.Method, context.Request.Path);

            if (context.Response.HasStarted)
                throw;

            context.Response.Clear();
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsJsonAsync(new
            {
                message = "An unexpected error occurred. Please try again or contact support."
            });
        }
    }
}

public static class ExceptionHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseCustomExceptionHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<ExceptionHandlingMiddleware>();
}
