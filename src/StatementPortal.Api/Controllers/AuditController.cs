using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StatementPortal.Api.Authorization;
using StatementPortal.Application.Audit;
using StatementPortal.Domain.Enums;

namespace StatementPortal.Api.Controllers;

/// <summary>
/// Epics 7 & 8 (US-12..US-16). GET-only by design: there is no PUT/PATCH/DELETE
/// action on this controller, and IAuditQueryService itself exposes no method
/// that could support one — audit records are read-only through this module at
/// every layer, not merely by omission here.
/// </summary>
[ApiController]
[Route("api/audit")]
[Authorize(Policy = PortalPolicies.Audit)]
public sealed class AuditController : ControllerBase
{
    private readonly IAuditQueryService _auditQueryService;

    public AuditController(IAuditQueryService auditQueryService) => _auditQueryService = auditQueryService;

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] DateOnly? startDate,
        [FromQuery] DateOnly? endDate,
        [FromQuery] string? staffNameOrId,
        [FromQuery] string? branch,
        [FromQuery] string? module,
        [FromQuery] string? activity,
        [FromQuery] AuditActivityStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var request = new AuditSearchRequest
        {
            StartDate = startDate,
            EndDate = endDate,
            StaffNameOrId = staffNameOrId,
            Branch = branch,
            Module = module,
            Activity = activity,
            Status = status,
            Page = page,
            PageSize = pageSize
        };

        var result = await _auditQueryService.SearchAsync(request, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id, CancellationToken cancellationToken)
    {
        var record = await _auditQueryService.GetByIdAsync(id, cancellationToken);
        return record is null ? NotFound() : Ok(record);
    }
}
