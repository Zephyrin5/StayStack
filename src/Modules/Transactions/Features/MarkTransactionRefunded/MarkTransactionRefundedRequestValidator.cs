using FastEndpoints;
using FluentValidation;
namespace Transactions.Features.MarkTransactionRefunded;

public sealed class MarkTransactionRefundedRequestValidator : Validator<MarkTransactionRefundedRequest>
{
    public MarkTransactionRefundedRequestValidator()
    {
        RuleFor(x => x.TransactionId).NotEmpty();
    }
}
