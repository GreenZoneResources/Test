namespace StatementPortal.Infrastructure.StatementService;

public sealed class StatementServiceOptions
{
    public const string SectionName = "StatementService";

    public string BaseUrl { get; set; } = default!;

    /// <summary>Service-to-service credential — loaded from secret storage, never appsettings.json.</summary>
    public string ApiKey { get; set; } = default!;

    public int TimeoutSeconds { get; set; } = 30;
}
