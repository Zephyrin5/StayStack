using FastEndpoints;
using FluentValidation;
namespace Transactions.Features.MarkTransactionFailed;

public sealed class MarkTransactionFailedRequestValidator : Validator<MarkTransactionFailedRequest>
{
    public MarkTransactionFailedRequestValidator()
    {
        RuleFor(x => x.TransactionId).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}
