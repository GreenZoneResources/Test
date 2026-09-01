using StatementPortal.Domain.Enums;

namespace StatementPortal.Application.Audit;

/// <summary>
/// Everything needed to write one immutable audit entry (Epic 6/US-10, US-11).
/// Account numbers must already be masked by the caller — see AccountNumberMasker —
/// this type has no notion of a plaintext account number to begin with.
/// </summary>
public sealed record AuditEntry(
    Guid RequestId,
    string StaffId,
    string StaffName,
    string Email,
    string? Branch,
    string Module,
    string Activity,
    string? MaskedAccountNumber,
    DateOnly? StatementStartDate,
    DateOnly? StatementEndDate,
    AuditActivityStatus Status,
    string? FailureReason,
    string IpAddress);

/// <summary>
/// Append-only. Deliberately has no Update or Delete method — US-16 requires audit
/// records to be immutable for every role, so there is no code path in this
/// application that can alter one once written.
/// </summary>
public interface IAuditWriter
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken);
}
