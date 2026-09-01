namespace StatementPortal.Domain.Enums;

/// <summary>
/// Portal modules gated by RBAC (Epic 2). Values are stable and must never be
/// renumbered once shipped — permission claims persist across sessions.
/// </summary>
public enum ModulePermission
{
    SingleStatement = 1,
    BulkStatement = 2,
    Audit = 3,

    /// <summary>
    /// Distinct from <see cref="Audit"/>: reserved for future audit-administration
    /// actions. No such actions exist today — Epic 8 (US-16) requires audit records
    /// to be immutable for every role, admins included, so this permission
    /// currently grants nothing beyond Audit read access.
    /// </summary>
    AuditAdmin = 4
}
