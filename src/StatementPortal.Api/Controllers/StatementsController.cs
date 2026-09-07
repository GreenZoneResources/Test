using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using StatementPortal.Api.Authorization;
using StatementPortal.Api.Models;
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
    // Rejected here, before the file is even opened for reading — cheaper
    // than letting a huge upload flow all the way to the Statement Service
    // only to fail there.
    private const long MaxBulkFileSizeBytes = 10 * 1024 * 1024;

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
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Bulk(
        [FromForm] BulkStatementUploadRequest request, CancellationToken cancellationToken)
    {
        if (request.File is null || request.File.Length == 0)
            return BadRequest(new { errors = new[] { "A file is required." } });

        if (request.File.Length > MaxBulkFileSizeBytes)
        {
            return BadRequest(new
            {
                errors = new[] { $"File exceeds the {MaxBulkFileSizeBytes / (1024 * 1024)} MB limit." }
            });
        }

        await using var fileStream = request.File.OpenReadStream();

        var dto = new BulkStatementRequestDto
        {
            Email = request.Email,
            FileContent = fileStream,
            FileName = request.File.FileName,
            ContentType = request.File.ContentType
        };

        try
        {
            var result = await _statementRequestService.SubmitBulkAsync(dto, cancellationToken);
            return Ok(result);
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { errors = ex.Errors.Select(e => e.ErrorMessage) });
        }
    }

    [HttpGet("sample-file")]
    [Authorize(Policy = PortalPolicies.BulkStatement)]
    public IActionResult SampleFile([FromQuery] string format = "csv")
    {
        // NOTE: this template's columns (AccountNumber, StartDate, EndDate) are
        // a placeholder — they match the fields a statement request logically
        // needs, but the Statement Service's actual expected bulk-file layout
        // hasn't been confirmed. Update this to match its real schema once
        // that's available; do not treat this as authoritative yet.
        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                errors = new[] { "Only format=csv is currently supported for the sample file." }
            });
        }

        const string csv = "AccountNumber,StartDate,EndDate\r\n1234567890,2026-01-01,2026-06-30\r\n";
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", "bulk-statement-sample.csv");
    }
}
