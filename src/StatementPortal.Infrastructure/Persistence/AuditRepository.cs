using Microsoft.EntityFrameworkCore;
using StatementPortal.Application.Audit;
using StatementPortal.Application.Common;
using StatementPortal.Domain.Entities;

namespace StatementPortal.Infrastructure.Persistence;

/// <summary>
/// Implements both the write side and the read side of the audit trail
/// (Epics 6-8). There is deliberately no method here that updates or deletes an
/// existing AuditRecord — the only mutation this class performs is Add.
/// Beyond the application layer, the database login used by this connection
/// string should itself be granted only INSERT/SELECT on AuditRecords (no
/// UPDATE/DELETE) so that even a future code defect can't tamper with history —
/// see the migration script's GRANT statements.
/// </summary>
public sealed class AuditRepository : IAuditWriter, IAuditQueryService
{
    private readonly StatementPortalDbContext _context;

    public AuditRepository(StatementPortalDbContext context) => _context = context;

    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        var record = new AuditRecord(
            entry.RequestId,
            entry.StaffId,
            entry.StaffName,
            entry.Email,
            entry.Branch,
            entry.Module,
            entry.Activity,
            entry.MaskedAccountNumber,
            entry.StatementStartDate,
            entry.StatementEndDate,
            entry.Status,
            entry.FailureReason,
            entry.IpAddress,
            DateTimeOffset.UtcNow);

        _context.AuditRecords.Add(record);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<AuditRecordDto>> SearchAsync(
        AuditSearchRequest request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = request.PageSize is <= 0 or > 100 ? 25 : request.PageSize;

        var query = _context.AuditRecords.AsNoTracking().AsQueryable();

        if (request.StartDate is { } start)
        {
            var startUtc = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(x => x.OccurredAtUtc >= startUtc);
        }

        if (request.EndDate is { } end)
        {
            var endExclusiveUtc = end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(x => x.OccurredAtUtc < endExclusiveUtc);
        }

        if (!string.IsNullOrWhiteSpace(request.StaffNameOrId))
        {
            var term = request.StaffNameOrId.Trim();
            query = query.Where(x => x.StaffName.Contains(term) || x.StaffId.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(request.Branch))
            query = query.Where(x => x.Branch == request.Branch);

        if (!string.IsNullOrWhiteSpace(request.Module))
            query = query.Where(x => x.Module == request.Module);

        if (!string.IsNullOrWhiteSpace(request.Activity))
            query = query.Where(x => x.Activity == request.Activity);

        if (request.Status is { } status)
            query = query.Where(x => x.Status == status);

        var totalCount = await query.CountAsync(cancellationToken);

        var entities = await query
            .OrderByDescending(x => x.OccurredAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditRecordDto>
        {
            Items = entities.Select(ToDto).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<AuditRecordDto?> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        var record = await _context.AuditRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        return record is null ? null : ToDto(record);
    }

    // Single mapping used by both the list and detail reads — the two DTOs
    // were merged into one shape at the frontend's request, so there's no
    // longer a reason for GetById to build a differently-shaped object.
    private static AuditRecordDto ToDto(AuditRecord record) => new()
    {
        Id = record.Id,
        RequestId = record.RequestId,
        StaffName = record.StaffName,
        StaffId = record.StaffId,
        Module = record.Module,
        Email = record.Email,
        Branch = record.Branch,
        Activity = record.Activity,
        MaskedAccountNumber = record.MaskedAccountNumber,
        StatementDateRange = FormatRange(record.StatementStartDate, record.StatementEndDate),
        Date = DateOnly.FromDateTime(record.OccurredAtUtc.UtcDateTime),
        Time = TimeOnly.FromDateTime(record.OccurredAtUtc.UtcDateTime),
        Status = record.Status.ToString(),
        IpAddress = record.IpAddress,
        FailureReason = record.FailureReason
    };

    private static string? FormatRange(DateOnly? start, DateOnly? end) =>
        start is null || end is null ? null : $"{start:yyyy-MM-dd} to {end:yyyy-MM-dd}";
}
