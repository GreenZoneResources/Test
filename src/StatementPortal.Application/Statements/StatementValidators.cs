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

public sealed class BulkStatementItemValidator : AbstractValidator<BulkStatementItemDto>
{
    public BulkStatementItemValidator()
    {
        RuleFor(x => x.AccountNumber).NotEmpty().Matches(@"^\d{10}$");

        RuleFor(x => x.EndDate)
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(DateTime.UtcNow));

        RuleFor(x => x)
            .Must(x => x.StartDate <= x.EndDate)
            .WithMessage("Start date must not be after end date.");
    }
}

public sealed class BulkStatementRequestValidator : AbstractValidator<BulkStatementRequestDto>
{
    // Keeps a single request bounded so one submission can't be used to exhaust
    // the downstream Statement Service or the portal's own request pipeline.
    private const int MaxBulkItems = 500;

    public BulkStatementRequestValidator()
    {
        RuleFor(x => x.Items)
            .NotEmpty()
            .WithMessage("At least one statement item is required.")
            .Must(items => items.Count <= MaxBulkItems)
            .WithMessage($"A bulk request cannot contain more than {MaxBulkItems} items.");

        RuleForEach(x => x.Items).SetValidator(new BulkStatementItemValidator());

        RuleFor(x => x.Format)
            .Must(f => f is "PDF" or "CSV")
            .WithMessage("Format must be PDF or CSV.");
    }
}
