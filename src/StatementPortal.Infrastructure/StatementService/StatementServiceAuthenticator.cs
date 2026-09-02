using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StatementPortal.Infrastructure.StatementService;

internal sealed class StatementAuthRequest
{
    public string Username { get; init; } = default!;
    public string Password { get; init; } = default!;
}

internal sealed class StatementAuthResponse
{
    public string Token { get; init; } = default!;
    public DateTimeOffset Expires { get; init; }
    public string TokenType { get; init; } = "Bearer";
}

/// <summary>
/// Obtains and caches the service-account bearer token used for every call to
/// the Statement Service. This is a machine credential (the portal
/// authenticating itself), not a staff member's session — it has nothing to
/// do with SSO/RBAC and callers never see it directly. The one place it's
/// consumed is StatementServiceAuthHandler, which attaches it to every
/// outgoing request automatically.
/// </summary>
public interface IStatementServiceAuthenticator
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

public sealed class StatementServiceAuthenticator : IStatementServiceAuthenticator
{
    // Refresh a little before actual expiry so a token never goes stale mid-flight
    // on a request that's already been dispatched.
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromSeconds(60);
    private const int MaxAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly StatementServiceOptions _options;
    private readonly ILogger<StatementServiceAuthenticator> _logger;

    // Guards refresh so concurrent requests hitting an expired/missing token
    // don't all fire a login call at once — only the first waiter refreshes;
    // the rest reuse the token it produces.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private (string Token, DateTimeOffset ExpiresAtUtc)? _cached;

    public StatementServiceAuthenticator(
        HttpClient httpClient, IOptions<StatementServiceOptions> options,
        ILogger<StatementServiceAuthenticator> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _httpClient.BaseAddress ??= new Uri(_options.BaseUrl);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } cached && cached.ExpiresAtUtc - ExpiryBuffer > DateTimeOffset.UtcNow)
            return cached.Token;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check: another caller may have already refreshed while we waited.
            if (_cached is { } refreshed && refreshed.ExpiresAtUtc - ExpiryBuffer > DateTimeOffset.UtcNow)
                return refreshed.Token;

            var token = await LoginAsync(cancellationToken);
            return token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<string> LoginAsync(CancellationToken cancellationToken)
    {
        var credentials = new StatementAuthRequest { Username = _options.Username, Password = _options.Password };

        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    _options.AuthPath, credentials, cancellationToken);

                // Bad credentials won't fix themselves on retry — fail fast and loud
                // rather than burning three attempts and a startup-blocking delay.
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException(
                        $"StatementService rejected the service-account credentials ({(int)response.StatusCode}).");
                }

                response.EnsureSuccessStatusCode();

                var auth = await response.Content.ReadFromJsonAsync<StatementAuthResponse>(cancellationToken)
                    ?? throw new InvalidOperationException("StatementService auth returned an empty response.");

                _cached = (auth.Token, auth.Expires);
                _logger.LogInformation(
                    "Authenticated with StatementService; token valid until {ExpiresAtUtc}.", auth.Expires);

                return auth.Token;
            }
            catch (InvalidOperationException)
            {
                // Credential rejection — see the 401/403 check above. Not retryable:
                // wrong credentials won't start being right on the next attempt.
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex,
                    "StatementService authentication attempt {Attempt}/{MaxAttempts} failed.",
                    attempt, MaxAttempts);
            }

            if (attempt < MaxAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
        }

        _logger.LogError(lastException,
            "StatementService authentication failed after {MaxAttempts} attempts.", MaxAttempts);
        throw new InvalidOperationException(
            "StatementService authentication failed — the service may be temporarily unavailable.", lastException);
    }
}
