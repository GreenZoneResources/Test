using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using StatementPortal.Api.Authorization;
using StatementPortal.Application.Statements;

namespace StatementPortal.Api.Controllers;

/// <summary>
/// Epics 3 & 4 (US-05..US-08). This controller — reached only through the
/// backend — is the sole caller of IStatementRequestService, which is in turn
/// the sole caller of IStatementServiceClient. There is no route anywhere that
/// lets a request reach the Statement Service without first passing through
/// the [Authorize(Policy = ...)] checks below (Epic 5/US-09).
/// </summary>
[ApiController]
[Route("api/statements")]
[Authorize]
[EnableRateLimiting("statements")]
public sealed class StatementsController : ControllerBase
{
    private readonly IStatementRequestService _statementRequestService;

    public StatementsController(IStatementRequestService statementRequestService) =>
        _statementRequestService = statementRequestService;

    [HttpPost("single")]
    [Authorize(Policy = PortalPolicies.SingleStatement)]
    public async Task<IActionResult> Single(
        [FromBody] SingleStatementRequestDto request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _statementRequestService.SubmitSingleAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { errors = ex.Errors.Select(e => e.ErrorMessage) });
        }
    }

    [HttpPost("bulk")]
    [Authorize(Policy = PortalPolicies.BulkStatement)]
    public async Task<IActionResult> Bulk(
        [FromBody] BulkStatementRequestDto request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _statementRequestService.SubmitBulkAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { errors = ex.Errors.Select(e => e.ErrorMessage) });
        }
    }
}
