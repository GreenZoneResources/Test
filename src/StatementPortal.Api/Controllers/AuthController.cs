using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using StatementPortal.Application.Permissions;
using StatementPortal.Infrastructure.Auth;
using StatementPortal.Infrastructure.Sso;

namespace StatementPortal.Api.Controllers;

/// <summary>
/// Implements Epic 1 (US-01, US-02) and Epic 10 (US-18) as a backend-for-
/// frontend flow against the existing LotusBank SSO service: the browser is
/// redirected to SSO, SSO performs the actual AD authentication, and this
/// controller exchanges the returned code for an identity server-to-server —
/// the SSO token itself never reaches the browser. What the browser receives
/// is this portal's own HttpOnly/Secure/SameSite session cookie.
/// </summary>
[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController : ControllerBase
{
    private const string PkceCookieName = "sso_pkce";

    private readonly ISsoClient _ssoClient;
    private readonly IPermissionService _permissionService;
    private readonly IDataProtector _protector;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        ISsoClient ssoClient,
        IPermissionService permissionService,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AuthController> logger)
    {
        _ssoClient = ssoClient;
        _permissionService = permissionService;
        _protector = dataProtectionProvider.CreateProtector("StatementPortal.Auth.Pkce");
        _logger = logger;
    }

    [HttpGet("login")]
    [AllowAnonymous]
    public IActionResult Login()
    {
        var state = PkceHelper.CreateState();
        var codeVerifier = PkceHelper.CreateCodeVerifier();
        var codeChallenge = PkceHelper.CreateCodeChallenge(codeVerifier);

        var payload = JsonSerializer.Serialize(new PkcePayload(state, codeVerifier));
        var protectedPayload = _protector.Protect(payload);

        Response.Cookies.Append(PkceCookieName, protectedPayload, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax, // Lax: must survive the top-level redirect back from SSO.
            MaxAge = TimeSpan.FromMinutes(10)
        });

        return Redirect(_ssoClient.BuildAuthorizeUrl(state, codeChallenge));
    }

    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(
        [FromQuery] string code, [FromQuery] string state, CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue(PkceCookieName, out var protectedPayload) ||
            string.IsNullOrEmpty(protectedPayload))
        {
            return BadRequest("Login session expired or was tampered with. Please sign in again.");
        }

        Response.Cookies.Delete(PkceCookieName);

        PkcePayload pkce;
        try
        {
            pkce = JsonSerializer.Deserialize<PkcePayload>(_protector.Unprotect(protectedPayload))!;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unprotect PKCE cookie during SSO callback.");
            return BadRequest("Invalid login state.");
        }

        // Constant-time-insensitive comparison is unnecessary here: 'state' is not
        // a secret, its role is CSRF binding between the two legs of the redirect.
        if (!string.Equals(pkce.State, state, StringComparison.Ordinal))
            return BadRequest("Login state mismatch.");

        var identity = await _ssoClient.ExchangeCodeAsync(code, pkce.CodeVerifier, cancellationToken);
        var permissions = await _permissionService.GetPermissionsAsync(identity.StaffId, cancellationToken);

        if (permissions.Count == 0)
        {
            // US-01 "Unauthorized staff access": authenticated by AD, but never
            // provisioned for this portal.
            return Forbid();
        }

        var claims = new List<Claim>
        {
            new(PortalClaimTypes.StaffId, identity.StaffId),
            new(PortalClaimTypes.StaffName, identity.StaffName),
            new(ClaimTypes.Email, identity.Email)
        };

        if (identity.Branch is not null)
            claims.Add(new Claim(PortalClaimTypes.Branch, identity.Branch));

        claims.AddRange(permissions.Select(p => new Claim(PortalClaimTypes.Permission, p)));

        var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(claimsIdentity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });

        _logger.LogInformation("Staff {StaffId} authenticated successfully via SSO.", identity.StaffId);

        return Redirect("/"); // landing page — served by the SPA/BFF host.
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var staffId = User.FindFirst(PortalClaimTypes.StaffId)?.Value;

        // Removes the server-side ticket via ITicketStore immediately (see
        // DistributedCacheTicketStore) — the cookie itself becomes worthless the
        // instant this returns, satisfying US-18's "access after logout" scenario
        // without waiting for cookie/ticket expiry.
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        _logger.LogInformation("Staff {StaffId} logged out.", staffId);

        return Ok(new { message = "Logged out." });
    }

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        return Ok(new
        {
            staffId = User.FindFirst(PortalClaimTypes.StaffId)?.Value,
            staffName = User.FindFirst(PortalClaimTypes.StaffName)?.Value,
            email = User.FindFirst(ClaimTypes.Email)?.Value,
            branch = User.FindFirst(PortalClaimTypes.Branch)?.Value,
            // US-04: tells the frontend which modules to render. This is a display
            // hint only — every protected endpoint re-checks the policy itself.
            permissions = User.FindAll(PortalClaimTypes.Permission).Select(c => c.Value)
        });
    }

    private sealed record PkcePayload(string State, string CodeVerifier);
}
