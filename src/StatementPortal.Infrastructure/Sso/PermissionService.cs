using Microsoft.Extensions.Caching.Memory;
using StatementPortal.Application.Permissions;

namespace StatementPortal.Infrastructure.Sso;

/// <summary>
/// Thin caching layer over SSO's permission lookup (US-03). Caching is short
/// (5 minutes) and per-staff-id keyed, so a permission revoked in the source
/// system takes effect quickly without hitting SSO on every single request.
/// </summary>
public sealed class PermissionService : IPermissionService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly ISsoClient _ssoClient;
    private readonly IMemoryCache _cache;

    public PermissionService(ISsoClient ssoClient, IMemoryCache cache)
    {
        _ssoClient = ssoClient;
        _cache = cache;
    }

    public async Task<IReadOnlyCollection<string>> GetPermissionsAsync(
        string staffId, CancellationToken cancellationToken)
    {
        var cacheKey = $"permissions:{staffId}";

        if (_cache.TryGetValue(cacheKey, out IReadOnlyCollection<string>? cached) && cached is not null)
            return cached;

        var permissions = await _ssoClient.GetPermissionsAsync(staffId, cancellationToken);
        _cache.Set(cacheKey, permissions, CacheDuration);
        return permissions;
    }
}
