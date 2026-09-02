using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace StatementPortal.Infrastructure.Auth;

/// <summary>
/// Runs immediately after UseAuthentication(). SSO's token carries an
/// "appRoles" claim listing every application the staff member has roles in
/// (ApplicationName + Roles[] per app), not just this one — so this
/// deliberately filters to the entry whose ApplicationName matches this
/// service's own identity (Authentication:ApplicationName) before turning
/// those roles into StatementPortal permission claims. Flattening every
/// app's roles together (as a naive port of the appRoles-parsing code would
/// do) would let a role name granted in a completely different application
/// leak into this one's authorization decisions.
///
/// This is what US-04's "backend must independently validate permissions"
/// requirement is grounded in: PermissionAuthorizationHandler only ever sees
/// claims this middleware adds here, never anything the client could set.
/// </summary>
public sealed class AppRolesClaimsMiddleware : IMiddleware
{
    private const string AppRolesClaimType = "appRoles";

    private readonly string _applicationName;
    private readonly ILogger<AppRolesClaimsMiddleware> _logger;

    public AppRolesClaimsMiddleware(IConfiguration configuration, ILogger<AppRolesClaimsMiddleware> logger)
    {
        _applicationName = configuration["Authentication:ApplicationName"]
            ?? throw new InvalidOperationException("Authentication:ApplicationName is not configured.");
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.User.Identity is ClaimsIdentity { IsAuthenticated: true } identity)
        {
            var staffId = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var appRolesClaim = identity.FindFirst(AppRolesClaimType)?.Value;

            if (string.IsNullOrWhiteSpace(appRolesClaim))
            {
                _logger.LogWarning(
                    "Rejecting request from {StaffId} to {Path}: token has no appRoles claim.",
                    staffId, context.Request.Path);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var permissions = ExtractPermissionsForThisApplication(appRolesClaim, staffId, context.Request.Path);

            if (permissions.Count == 0)
            {
                _logger.LogWarning(
                    "Rejecting request from {StaffId} to {Path}: no {ApplicationName} roles in token.",
                    staffId, context.Request.Path, _applicationName);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            foreach (var permission in permissions)
                identity.AddClaim(new Claim(PortalClaimTypes.Permission, permission));
        }

        await next(context);
    }

    private List<string> ExtractPermissionsForThisApplication(string appRolesJson, string? staffId, PathString path)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<GroupedRoleEntry>>(
                appRolesJson, JsonSerializerOptions.Web);

            var thisAppEntry = entries?.FirstOrDefault(e =>
                string.Equals(e.ApplicationName, _applicationName, StringComparison.OrdinalIgnoreCase));

            return thisAppEntry?.Roles.ToList() ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Failed to parse appRoles claim for {StaffId} on request to {Path}.", staffId, path);
            return [];
        }
    }

    private sealed class GroupedRoleEntry
    {
        public string ApplicationName { get; set; } = string.Empty;
        public string[] Roles { get; set; } = [];
    }
}
