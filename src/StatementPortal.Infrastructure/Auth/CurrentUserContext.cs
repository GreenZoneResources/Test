using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using StatementPortal.Application.Common;

namespace StatementPortal.Infrastructure.Auth;

public static class PortalClaimTypes
{
    /// <summary>
    /// Not present on the incoming SSO token — added locally by
    /// AppRolesClaimsMiddleware after filtering the token's "appRoles" claim
    /// down to this application's own roles. This is the only claim type this
    /// service itself ever adds; everything else is read as SSO put it there.
    /// </summary>
    public const string Permission = "permission";

    /// <summary>
    /// Best-effort guess pending confirmation against a real SSO-issued token —
    /// the pasted ServiceManager only confirms NameIdentifier (staff id) and
    /// appRoles. Adjust this to whatever claim actually carries branch, if any.
    /// </summary>
    public const string Branch = "branch";
}

/// <summary>
/// Reads identity strictly from the ClaimsPrincipal the JWT Bearer handler
/// builds from SSO's token (see AuthenticationExtensions) plus the locally-added
/// permission claims (see AppRolesClaimsMiddleware) — never from anything a
/// client could set on the request itself. This is the only implementation of
/// ICurrentUserContext in the solution, so every audit entry and permission
/// check is grounded in the same trusted source.
/// </summary>
public sealed class CurrentUserContext : ICurrentUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserContext(IHttpContextAccessor httpContextAccessor) =>
        _httpContextAccessor = httpContextAccessor;

    private HttpContext Context =>
        _httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No active HTTP context.");

    // NameClaimType is configured to ClaimTypes.NameIdentifier in
    // JwtSigningKeyProvider, matching SSO's own TokenValidationParameters.
    public string StaffId => RequireClaim(ClaimTypes.NameIdentifier);

    public string StaffName => Context.User.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;

    public string Email => Context.User.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;

    public string? Branch => Context.User.FindFirst(PortalClaimTypes.Branch)?.Value;

    public string IpAddress => Context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public IReadOnlyCollection<string> Permissions =>
        Context.User.FindAll(PortalClaimTypes.Permission).Select(c => c.Value).ToArray();

    public bool HasPermission(string permission) =>
        Context.User.HasClaim(PortalClaimTypes.Permission, permission);

    private string RequireClaim(string claimType) =>
        Context.User.FindFirst(claimType)?.Value
            ?? throw new InvalidOperationException($"Authenticated principal is missing claim '{claimType}'.");
}
