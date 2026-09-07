using FluentValidation;

namespace StatementPortal.Application.Statements;

/// <summary>
/// US-06/US-08 "invalid statement request" scenarios: every rule here runs
/// before the Statement Service is ever invoked, and a failure here means the
/// service call never happens.
/// </summary>
public sealed class SingleStatementRequestValidator : AbstractValidator<SingleStatementRequestDto>
{
    public SingleStatementRequestValidator()
    {
        RuleFor(x => x.AccountNumber)
            .NotEmpty()
            .Matches(@"^\d{10}$")
            .WithMessage("Account number must be a 10-digit account number.");

        RuleFor(x => x.EndDate)
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("End date cannot be in the future.");

        RuleFor(x => x)
            .Must(x => x.StartDate <= x.EndDate)
            .WithMessage("Start date must not be after end date.")
            .Must(x => x.EndDate.DayNumber - x.StartDate.DayNumber <= 366)
            .WithMessage("Statement period cannot exceed 12 months.");

        RuleFor(x => x.Format)
            .Must(f => f is "PDF" or "CSV")
            .WithMessage("Format must be PDF or CSV.");
    }
}

public sealed class BulkStatementRequestValidator : AbstractValidator<BulkStatementRequestDto>
{
    private static readonly string[] AllowedContentTypes =
    [
        "text/csv",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    ];

    public BulkStatementRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .WithMessage("A valid notification email is required.");

        RuleFor(x => x.FileName)
            .NotEmpty()
            .WithMessage("The uploaded file must have a file name.");

        RuleFor(x => x.ContentType)
            .Must(ct => AllowedContentTypes.Contains(ct, StringComparer.OrdinalIgnoreCase))
            .WithMessage("File must be a CSV or Excel (.xlsx/.xls) file.");

        // File size is checked in the controller, before the stream is ever
        // read here — see StatementsController.Bulk — since that's the
        // cheapest place to reject an oversized upload.
    }
}
