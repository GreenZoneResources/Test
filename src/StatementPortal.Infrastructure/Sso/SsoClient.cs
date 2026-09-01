using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace StatementPortal.Infrastructure.Sso;

public sealed record SsoIdentity(string StaffId, string StaffName, string Email, string? Branch);

public interface ISsoClient
{
    string BuildAuthorizeUrl(string state, string codeChallenge);

    Task<SsoIdentity> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<string>> GetPermissionsAsync(string staffId, CancellationToken cancellationToken);
}

/// <summary>
/// Server-to-server (backend-for-frontend) integration with the existing SSO
/// service: the browser only ever sees an authorization "code" on the redirect
/// and this portal's own session cookie afterwards — the SSO-issued token
/// itself is exchanged, validated, and discarded entirely inside this class.
/// It never reaches client-side JavaScript, which is exactly what US-02
/// requires of the session.
/// </summary>
public sealed class SsoClient : ISsoClient
{
    private const string JwksCacheKey = "sso-jwks";

    private readonly HttpClient _httpClient;
    private readonly SsoOptions _options;
    private readonly IMemoryCache _cache;

    public SsoClient(HttpClient httpClient, IOptions<SsoOptions> options, IMemoryCache cache)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _cache = cache;
    }

    public string BuildAuthorizeUrl(string state, string codeChallenge)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid profile email",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };

        return QueryHelpers.AddQueryString(_options.AuthorizeEndpoint, query);
    }

    public async Task<SsoIdentity> ExchangeCodeAsync(
        string code, string codeVerifier, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(
            _options.TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = _options.RedirectUri,
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["code_verifier"] = codeVerifier
            }),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("SSO rejected the authorization code exchange.");

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new InvalidOperationException("SSO token response was empty.");

        var principal = await ValidateTokenAsync(payload.IdToken, cancellationToken);

        var staffId = principal.FindFirst("sub")?.Value
            ?? throw new SecurityTokenException("SSO token is missing the 'sub' claim.");

        return new SsoIdentity(
            StaffId: staffId,
            StaffName: principal.FindFirst("name")?.Value ?? string.Empty,
            Email: principal.FindFirst("email")?.Value ?? string.Empty,
            Branch: principal.FindFirst("branch")?.Value);
    }

    public async Task<IReadOnlyCollection<string>> GetPermissionsAsync(
        string staffId, CancellationToken cancellationToken)
    {
        // Permissions come from SSO/entitlement store on every new login (and are
        // cached briefly upstream by IPermissionService) — never from a client-
        // supplied claim, per US-04's backend-control requirement.
        using var response = await _httpClient.GetAsync(
            $"{_options.PermissionsEndpoint.TrimEnd('/')}/{Uri.EscapeDataString(staffId)}",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            return Array.Empty<string>();

        var permissions = await response.Content.ReadFromJsonAsync<string[]>(cancellationToken);
        return permissions ?? Array.Empty<string>();
    }

    private async Task<ClaimsPrincipal> ValidateTokenAsync(string idToken, CancellationToken cancellationToken)
    {
        var keySet = await GetSigningKeysAsync(cancellationToken);

        var handler = new JwtSecurityTokenHandler();
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = _options.Issuer,
            ValidAudience = _options.Audience,
            IssuerSigningKeys = keySet.GetSigningKeys(),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        // Throws SecurityTokenException on any validation failure (bad signature,
        // wrong issuer/audience, expired token) — callers must not catch and
        // silently continue on failure here.
        return handler.ValidateToken(idToken, parameters, out _);
    }

    private async Task<JsonWebKeySet> GetSigningKeysAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(JwksCacheKey, out JsonWebKeySet? cached) && cached is not null)
            return cached;

        var json = await _httpClient.GetStringAsync(_options.JwksUri, cancellationToken);
        var keySet = new JsonWebKeySet(json);

        _cache.Set(JwksCacheKey, keySet, TimeSpan.FromMinutes(15));
        return keySet;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("id_token")] string IdToken,
        [property: JsonPropertyName("access_token")] string? AccessToken);
}
