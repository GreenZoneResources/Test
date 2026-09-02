using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StatementPortal.Infrastructure.Auth;

namespace StatementPortal.Api.Controllers;

/// <summary>
/// This service no longer has login/callback/logout endpoints — SSO owns the
/// whole authentication lifecycle, and this API only ever validates the
/// bearer token it already issued (see AuthenticationExtensions). All that's
/// left for the frontend to ask this service is "given my current token,
/// what can I see" — US-04's module-visibility hint, read straight off the
/// claims AppRolesClaimsMiddleware already attached, with no extra network
/// call to anywhere.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            staffId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            staffName = User.FindFirst(ClaimTypes.Name)?.Value,
            email = User.FindFirst(ClaimTypes.Email)?.Value,
            // Display hint only — every protected endpoint re-checks the
            // policy itself regardless of what this list says.
            permissions = User.FindAll(PortalClaimTypes.Permission).Select(c => c.Value)
        });
    }
}
