using StatementPortal.Domain.Enums;

namespace StatementPortal.Domain.Entities;

/// <summary>
/// An append-only audit trail entry (Epic 6/8). By design this type exposes no
/// Update/Delete surface anywhere — every property is set once, at construction.
/// US-16 requires audit evidence to be immutable through the application; that
/// guarantee starts here, at the type itself, not merely at an authorization check.
/// </summary>
public sealed class AuditRecord
{
    public long Id { get; private set; }
    public Guid RequestId { get; private set; }
    public string StaffId { get; private set; } = default!;
    public string StaffName { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public string? Branch { get; private set; }
    public string Module { get; private set; } = default!;
    public string Activity { get; private set; } = default!;
    public string? MaskedAccountNumber { get; private set; }
    public DateOnly? StatementStartDate { get; private set; }
    public DateOnly? StatementEndDate { get; private set; }
    public AuditActivityStatus Status { get; private set; }
    public string? FailureReason { get; private set; }
    public string IpAddress { get; private set; } = default!;
    public DateTimeOffset OccurredAtUtc { get; private set; }

    // EF Core materialization constructor.
    private AuditRecord() { }

    public AuditRecord(
        Guid requestId,
        string staffId,
        string staffName,
        string email,
        string? branch,
        string module,
        string activity,
        string? maskedAccountNumber,
        DateOnly? statementStartDate,
        DateOnly? statementEndDate,
        AuditActivityStatus status,
        string? failureReason,
        string ipAddress,
        DateTimeOffset occurredAtUtc)
    {
        RequestId = requestId;
        StaffId = staffId;
        StaffName = staffName;
        Email = email;
        Branch = branch;
        Module = module;
        Activity = activity;
        MaskedAccountNumber = maskedAccountNumber;
        StatementStartDate = statementStartDate;
        StatementEndDate = statementEndDate;
        Status = status;
        FailureReason = failureReason;
        IpAddress = ipAddress;
        OccurredAtUtc = occurredAtUtc;
    }
}
