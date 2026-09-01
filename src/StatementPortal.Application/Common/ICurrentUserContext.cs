namespace StatementPortal.Application.Common;

/// <summary>
/// Resolves the acting staff member's identity for the current request from the
/// authenticated session — never from anything client-supplied (headers, body
/// fields). Every audited action and every permission check reads through this
/// abstraction so there is exactly one place identity can be spoofed from, and
/// it isn't the request payload.
/// </summary>
public interface ICurrentUserContext
{
    string StaffId { get; }
    string StaffName { get; }
    string Email { get; }
    string? Branch { get; }

    /// <summary>Resolved server-side from the trusted proxy chain — see ForwardedHeadersOptions.</summary>
    string IpAddress { get; }

    IReadOnlyCollection<string> Permissions { get; }

    bool HasPermission(string permission);
}
