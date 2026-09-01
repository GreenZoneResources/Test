using Microsoft.AspNetCore.Authorization;
using StatementPortal.Domain.Enums;
using StatementPortal.Infrastructure.Auth;

namespace StatementPortal.Api.Authorization;

/// <summary>
/// US-17: every protected endpoint declares the permission it requires via
/// [Authorize(Policy = ...)], and this handler is the single place that
/// decision is made. There is no controller in this project that checks
/// permissions ad hoc — that's what US-04's "frontend visibility is not
/// authorization" note is guarding against: one enforcement point, always run,
/// regardless of what the UI does or doesn't show.
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(ModulePermission permission) => Permission = permission;

    public ModulePermission Permission { get; }
}

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasClaim(PortalClaimTypes.Permission, requirement.Permission.ToString()))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

public static class PortalPolicies
{
    public const string SingleStatement = nameof(ModulePermission.SingleStatement);
    public const string BulkStatement = nameof(ModulePermission.BulkStatement);
    public const string Audit = nameof(ModulePermission.Audit);

    public static void AddPortalPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(SingleStatement, p =>
            p.Requirements.Add(new PermissionRequirement(ModulePermission.SingleStatement)));

        options.AddPolicy(BulkStatement, p =>
            p.Requirements.Add(new PermissionRequirement(ModulePermission.BulkStatement)));

        options.AddPolicy(Audit, p =>
            p.Requirements.Add(new PermissionRequirement(ModulePermission.Audit)));

        // Everything not explicitly [AllowAnonymous] requires at minimum an
        // authenticated session — see Program.cs FallbackPolicy.
    }
}
