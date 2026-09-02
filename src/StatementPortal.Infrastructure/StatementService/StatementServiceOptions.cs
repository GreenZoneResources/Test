namespace StatementPortal.Infrastructure.StatementService;

public sealed class StatementServiceOptions
{
    public const string SectionName = "StatementService";

    public string BaseUrl { get; set; } = default!;

    /// <summary>Relative path appended to BaseUrl for the service-account login call.</summary>
    public string AuthPath { get; set; } = "api/auth/login";

    /// <summary>Service-account credentials — loaded from secret storage, never appsettings.json.</summary>
    public string Username { get; set; } = default!;

    public string Password { get; set; } = default!;

    public int TimeoutSeconds { get; set; } = 30;
}
