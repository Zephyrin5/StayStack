using FastEndpoints;
using FluentValidation;
namespace Transactions.Features.MarkTransactionSucceeded;

public sealed class MarkTransactionSucceededRequestValidator : Validator<MarkTransactionSucceededRequest>
{
    public MarkTransactionSucceededRequestValidator()
    {
        RuleFor(x => x.TransactionId).NotEmpty();
    }
}
