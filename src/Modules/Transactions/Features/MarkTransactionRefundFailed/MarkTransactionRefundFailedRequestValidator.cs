using FastEndpoints;
using FluentValidation;
namespace Transactions.Features.MarkTransactionRefundFailed;

public sealed class MarkTransactionRefundFailedRequestValidator : Validator<MarkTransactionRefundFailedRequest>
{
    public MarkTransactionRefundFailedRequestValidator()
    {
        RuleFor(x => x.TransactionId).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}
