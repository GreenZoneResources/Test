using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace StatementPortal.Infrastructure.Auth;

/// <summary>
/// Loads the shared JWT signing key/issuer/audience that LotusBank SSO signs
/// its tokens with, from the same database SSO itself reads via
/// [dbo].[GetTokenSettings] — this service validates tokens locally instead
/// of calling back to SSO over the network at request time.
///
/// Deliberately NOT loaded via `builder.Services.BuildServiceProvider()...Result`
/// at host-startup time (the pattern in the pasted ServiceManager): that blocks
/// the whole app from starting on a synchronous DB round trip, and spins up a
/// throwaway second container just to fetch one value. Instead this loads
/// lazily and asynchronously the first time a token actually needs validating,
/// cached (double-checked locking via SemaphoreSlim) for the rest of the
/// process's lifetime.
/// </summary>
public interface IJwtSigningKeyProvider
{
    Task<TokenValidationParameters> GetTokenValidationParametersAsync(CancellationToken cancellationToken);
}

public sealed class JwtSigningKeyProvider : IJwtSigningKeyProvider
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<JwtSigningKeyProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TokenValidationParameters? _cached;

    public JwtSigningKeyProvider(IConfiguration configuration, ILogger<JwtSigningKeyProvider> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<TokenValidationParameters> GetTokenValidationParametersAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
            return _cached;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Another request may have already populated this while we waited.
            if (_cached is not null)
                return _cached;

            var (issuer, audience, key) = await LoadFromDatabaseAsync(cancellationToken);

            _cached = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                RequireSignedTokens = true,
                RoleClaimType = System.Security.Claims.ClaimTypes.Role,
                NameClaimType = System.Security.Claims.ClaimTypes.NameIdentifier
            };

            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<(string Issuer, string Audience, byte[] Key)> LoadFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString("SingleSignOnConnection")
            ?? throw new InvalidOperationException("Connection string 'SingleSignOnConnection' is not configured.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[dbo].[GetTokenSettings]", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 30
        };

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("GetTokenSettings returned no rows.");

        var issuer = GetRequiredString(reader, "Issuer");
        var audience = GetRequiredString(reader, "Audience");
        var keyBase64 = GetRequiredString(reader, "Key");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(keyBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("JWT signing key is not valid Base64.", ex);
        }

        _logger.LogInformation("Loaded JWT signing settings for issuer {Issuer}.", issuer);
        return (issuer, audience, key);
    }

    private static string GetRequiredString(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        var value = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal)?.ToString()?.Trim();

        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"GetTokenSettings returned an empty '{columnName}'.")
            : value;
    }
}
