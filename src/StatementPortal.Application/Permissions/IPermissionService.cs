namespace StatementPortal.Application.Permissions;

/// <summary>
/// Resolves a staff member's module permissions (Epic 2/US-03). The backend is
/// the sole source of truth here — US-04's "Important Backend Control" note
/// means nothing that reads from this interface may be bypassed by trusting a
/// client-supplied role/permission claim instead.
/// </summary>
public interface IPermissionService
{
    Task<IReadOnlyCollection<string>> GetPermissionsAsync(string staffId, CancellationToken cancellationToken);
}
