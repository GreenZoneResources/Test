using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Distributed;

namespace StatementPortal.Infrastructure.Auth;

/// <summary>
/// Server-side session store for cookie authentication (US-02, US-18).
///
/// Plain ASP.NET Core cookie auth is self-contained: the cookie itself carries
/// the encrypted ticket, and the server can't truly revoke one before its
/// expiry — which fails US-02's "session expiry" and US-18's "access after
/// logout" scenarios, both of which require a *rejected* request, not just an
/// expired-but-still-cryptographically-valid cookie. Registering this as
/// CookieAuthenticationOptions.SessionStore fixes that: the cookie becomes an
/// opaque key, the actual ticket lives server-side (Redis in production via
/// IDistributedCache), and logout/expiry delete it immediately — the next
/// request with that cookie finds nothing and is rejected right away.
/// </summary>
public sealed class DistributedCacheTicketStore : ITicketStore
{
    private const string KeyPrefix = "auth-session:";

    private readonly IDistributedCache _cache;
    private readonly TicketSerializer _serializer = TicketSerializer.Default;

    public DistributedCacheTicketStore(IDistributedCache cache) => _cache = cache;

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = KeyPrefix + Guid.NewGuid().ToString("N");
        await RenewAsync(key, ticket);
        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var bytes = _serializer.Serialize(ticket);

        var options = new DistributedCacheEntryOptions();
        var expiresUtc = ticket.Properties.ExpiresUtc;
        if (expiresUtc.HasValue)
            options.SetAbsoluteExpiration(expiresUtc.Value);

        await _cache.SetAsync(key, bytes, options);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var bytes = await _cache.GetAsync(key);
        return bytes is null ? null : _serializer.Deserialize(bytes);
    }

    public Task RemoveAsync(string key) => _cache.RemoveAsync(key);
}
