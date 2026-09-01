using FluentValidation;
using StatementPortal.Application.Audit;
using StatementPortal.Application.Common;
using StatementPortal.Domain.Enums;

namespace StatementPortal.Application.Statements;

public interface IStatementRequestService
{
    Task<StatementResultDto> SubmitSingleAsync(SingleStatementRequestDto request, CancellationToken cancellationToken);

    Task<BulkStatementResultDto> SubmitBulkAsync(BulkStatementRequestDto request, CancellationToken cancellationToken);
}

/// <summary>
/// Orchestrates Epics 3-6 for both statement flows: validate, generate the
/// authoritative Request ID server-side (US-06/US-08 — never trust a
/// client-supplied one), call the Statement Service, and record exactly one
/// audit entry per request reflecting its final outcome. Permission checks
/// happen one layer up, at the controller's [Authorize(Policy = ...)] — by the
/// time a call reaches here the caller is already known to hold the module
/// permission, so this service never re-derives that decision.
/// </summary>
public sealed class StatementRequestService : IStatementRequestService
{
    private readonly IStatementServiceClient _client;
    private readonly IAuditWriter _auditWriter;
    private readonly ICurrentUserContext _currentUser;
    private readonly IValidator<SingleStatementRequestDto> _singleValidator;
    private readonly IValidator<BulkStatementRequestDto> _bulkValidator;

    public StatementRequestService(
        IStatementServiceClient client,
        IAuditWriter auditWriter,
        ICurrentUserContext currentUser,
        IValidator<SingleStatementRequestDto> singleValidator,
        IValidator<BulkStatementRequestDto> bulkValidator)
    {
        _client = client;
        _auditWriter = auditWriter;
        _currentUser = currentUser;
        _singleValidator = singleValidator;
        _bulkValidator = bulkValidator;
    }

    public async Task<StatementResultDto> SubmitSingleAsync(
        SingleStatementRequestDto request, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var validation = await _singleValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            await _auditWriter.RecordAsync(
                BuildEntry(requestId, "SingleStatement", "GenerateStatement", request.AccountNumber,
                    request.StartDate, request.EndDate, AuditActivityStatus.Failed,
                    string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))),
                cancellationToken);

            throw new ValidationException(validation.Errors);
        }

        try
        {
            var result = await _client.GenerateSingleAsync(requestId, request, cancellationToken);

            await _auditWriter.RecordAsync(
                BuildEntry(requestId, "SingleStatement", "GenerateStatement", request.AccountNumber,
                    request.StartDate, request.EndDate, AuditActivityStatus.Success, failureReason: null),
                cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            await _auditWriter.RecordAsync(
                BuildEntry(requestId, "SingleStatement", "GenerateStatement", request.AccountNumber,
                    request.StartDate, request.EndDate, AuditActivityStatus.Failed, ex.Message),
                cancellationToken);

            throw;
        }
    }

    public async Task<BulkStatementResultDto> SubmitBulkAsync(
        BulkStatementRequestDto request, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var validation = await _bulkValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            await _auditWriter.RecordAsync(
                BuildEntry(requestId, "BulkStatement", "SubmitBulkStatement", accountNumber: null,
                    start: null, end: null, AuditActivityStatus.Failed,
                    string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))),
                cancellationToken);

            throw new ValidationException(validation.Errors);
        }

        try
        {
            var result = await _client.SubmitBulkAsync(requestId, request, cancellationToken);

            await _auditWriter.RecordAsync(
                BuildEntry(requestId, "BulkStatement", "SubmitBulkStatement", accountNumber: null,
                    start: null, end: null, AuditActivityStatus.Success, failureReason: null),
                cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            await _auditWriter.RecordAsync(
                BuildEntry(requestId, "BulkStatement", "SubmitBulkStatement", accountNumber: null,
                    start: null, end: null, AuditActivityStatus.Failed, ex.Message),
                cancellationToken);

            throw;
        }
    }

    private AuditEntry BuildEntry(
        Guid requestId,
        string module,
        string activity,
        string? accountNumber,
        DateOnly? start,
        DateOnly? end,
        AuditActivityStatus status,
        string? failureReason) =>
        new(
            requestId,
            _currentUser.StaffId,
            _currentUser.StaffName,
            _currentUser.Email,
            _currentUser.Branch,
            module,
            activity,
            accountNumber is null ? null : AccountNumberMasker.Mask(accountNumber),
            start,
            end,
            status,
            failureReason,
            _currentUser.IpAddress);
}
