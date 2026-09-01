using System.Security.Cryptography;

namespace StatementPortal.Infrastructure.Sso;

/// <summary>
/// PKCE (RFC 7636) for the authorization-code exchange with SSO. This closes
/// the classic authorization-code-interception gap: even if an attacker
/// captured the redirect URI's "code" parameter, they cannot exchange it
/// without the original code_verifier, which never leaves the server.
/// </summary>
public static class PkceHelper
{
    public static string CreateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    public static string CreateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    public static string CreateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(24));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
