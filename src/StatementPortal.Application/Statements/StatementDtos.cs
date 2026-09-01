namespace StatementPortal.Application.Statements;

public sealed class SingleStatementRequestDto
{
    public string AccountNumber { get; init; } = default!;
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public string Format { get; init; } = "PDF";
}

public sealed class BulkStatementItemDto
{
    public string AccountNumber { get; init; } = default!;
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
}

public sealed class BulkStatementRequestDto
{
    public IReadOnlyList<BulkStatementItemDto> Items { get; init; } = Array.Empty<BulkStatementItemDto>();
    public string Format { get; init; } = "PDF";
}

public sealed class StatementResultDto
{
    public Guid RequestId { get; init; }
    public string Status { get; init; } = default!;
    public string? DownloadUrl { get; init; }
}

public sealed class BulkStatementResultDto
{
    public Guid RequestId { get; init; }
    public string Status { get; init; } = default!;
    public int AcceptedCount { get; init; }
    public int RejectedCount { get; init; }
}

/// <summary>
/// Abstraction over the existing Statement Service (Epic 5/US-09). The portal
/// backend is the only caller of this interface — nothing in the frontend or
/// any other layer talks to the Statement Service directly, which is precisely
/// what US-09's "direct frontend access" scenario prohibits.
/// </summary>
public interface IStatementServiceClient
{
    Task<StatementResultDto> GenerateSingleAsync(
        Guid requestId, SingleStatementRequestDto request, CancellationToken cancellationToken);

    Task<BulkStatementResultDto> SubmitBulkAsync(
        Guid requestId, BulkStatementRequestDto request, CancellationToken cancellationToken);
}
