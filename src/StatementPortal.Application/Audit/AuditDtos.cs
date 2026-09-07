using StatementPortal.Application.Common;
using StatementPortal.Domain.Enums;

namespace StatementPortal.Application.Audit;

/// <summary>
/// Row shape for both US-13's audit table and US-15's detail view — the two
/// were previously separate types (AuditRecordDto / AuditRecordDetailDto),
/// merged into one per the frontend's request so GET /api/audit and
/// GET /api/audit/{id} return the same item shape either way.
/// </summary>
public sealed class AuditRecordDto
{
    public long Id { get; init; }
    public Guid RequestId { get; init; }
    public string StaffName { get; init; } = default!;
    public string StaffId { get; init; } = default!;
    public string Module { get; init; } = default!;
    public string Email { get; init; } = default!;
    public string? Branch { get; init; }
    public string Activity { get; init; } = default!;
    public string? MaskedAccountNumber { get; init; }
    public string? StatementDateRange { get; init; }
    public DateOnly Date { get; init; }
    public TimeOnly Time { get; init; }
    public string Status { get; init; } = default!;
    public string IpAddress { get; init; } = default!;

    /// <summary>Only meaningful when Status is Failed/Denied; null on a successful activity.</summary>
    public string? FailureReason { get; init; }
}

/// <summary>US-14's combined filter set — all filters are ANDed together.</summary>
public sealed class AuditSearchRequest
{
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string? StaffNameOrId { get; init; }
    public string? Branch { get; init; }
    public string? Module { get; init; }
    public string? Activity { get; init; }
    public AuditActivityStatus? Status { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

/// <summary>Read-only by construction — no Update/Delete method exists here either.</summary>
public interface IAuditQueryService
{
    Task<PagedResult<AuditRecordDto>> SearchAsync(AuditSearchRequest request, CancellationToken cancellationToken);

    Task<AuditRecordDto?> GetByIdAsync(long id, CancellationToken cancellationToken);
}
