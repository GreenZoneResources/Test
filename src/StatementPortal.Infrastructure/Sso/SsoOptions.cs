namespace StatementPortal.Infrastructure.Sso;

/// <summary>
/// Wiring for the existing LotusBank SingleSignOn service. This portal does not
/// implement AD authentication itself — it delegates to SSO (which already
/// handles that) and only consumes the resulting identity, per the decision to
/// reuse the existing service rather than duplicate AD/Graph integration.
/// Adjust endpoint shapes here to match SSO's actual OAuth2/OIDC surface.
/// </summary>
public sealed class SsoOptions
{
    public const string SectionName = "Sso";

    public string AuthorizeEndpoint { get; set; } = default!;
    public string TokenEndpoint { get; set; } = default!;
    public string PermissionsEndpoint { get; set; } = default!;
    public string JwksUri { get; set; } = default!;
    public string Issuer { get; set; } = default!;
    public string Audience { get; set; } = default!;

    public string ClientId { get; set; } = default!;

    /// <summary>Loaded from secret storage (User Secrets / Key Vault / env var) — never appsettings.json.</summary>
    public string ClientSecret { get; set; } = default!;

    public string RedirectUri { get; set; } = default!;
    public string PostLogoutRedirectUri { get; set; } = default!;
}
