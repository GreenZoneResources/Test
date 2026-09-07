using Microsoft.AspNetCore.Http;

namespace StatementPortal.Api.Models;

/// <summary>
/// The multipart/form-data shape POST /api/statements/bulk actually binds.
/// IFormFile is an ASP.NET Core type, so this lives at the API layer, not in
/// StatementPortal.Application — StatementsController maps it onto the
/// framework-agnostic BulkStatementRequestDto before calling into the
/// application layer.
/// </summary>
public sealed class BulkStatementUploadRequest
{
    public string Email { get; init; } = default!;
    public IFormFile File { get; init; } = default!;
}
