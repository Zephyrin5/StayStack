using FastEndpoints;
using FluentValidation;
namespace Transactions.Features.InitiateTransaction;

public sealed class InitiateTransactionRequestValidator : Validator<InitiateTransactionRequest>
{
    public InitiateTransactionRequestValidator()
    {
        RuleFor(x => x.BookingId).NotEmpty();
    }
}
