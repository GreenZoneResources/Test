using Microsoft.AspNetCore.Http;
using StatementPortal.Application.Common;

namespace StatementPortal.Infrastructure.Auth;

public static class PortalClaimTypes
{
    public const string StaffId = "staff_id";
    public const string StaffName = "staff_name";
    public const string Branch = "branch";
    public const string Permission = "permission";
}

/// <summary>
/// Reads identity strictly from the authenticated ClaimsPrincipal built at
/// sign-in (see AuthController) and from the server-resolved remote IP — never
/// from request headers/body a client could set arbitrarily. This is the only
/// implementation of ICurrentUserContext in the solution, so every audit entry
/// and permission check is grounded in the same trusted source.
/// </summary>
public sealed class CurrentUserContext : ICurrentUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserContext(IHttpContextAccessor httpContextAccessor) =>
        _httpContextAccessor = httpContextAccessor;

    private HttpContext Context =>
        _httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No active HTTP context.");

    public string StaffId => RequireClaim(PortalClaimTypes.StaffId);

    public string StaffName => RequireClaim(PortalClaimTypes.StaffName);

    public string Email => Context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? string.Empty;

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
