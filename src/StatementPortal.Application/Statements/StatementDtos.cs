namespace StatementPortal.Application.Statements;

public sealed class SingleStatementRequestDto
{
    public string AccountNumber { get; init; } = default!;
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public string Format { get; init; } = "PDF";
}

/// <summary>
/// Bulk submission is a file upload (an account list, format owned by the
/// Statement Service) plus a notification email — not a JSON array of items.
/// This service does not parse the file; it forwards it to the Statement
/// Service as-is (see IStatementServiceClient.SubmitBulkAsync), so this DTO
/// only needs enough to do that forwarding. It uses Stream rather than
/// ASP.NET Core's IFormFile deliberately — this project (Application) has no
/// framework dependency, and the API layer (StatementsController) is where
/// IFormFile gets unwrapped into this shape.
/// </summary>
public sealed class BulkStatementRequestDto
{
    public string Email { get; init; } = default!;
    public Stream FileContent { get; init; } = default!;
    public string FileName { get; init; } = default!;
    public string ContentType { get; init; } = default!;
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
