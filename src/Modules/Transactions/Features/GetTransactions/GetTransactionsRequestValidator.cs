using BuildingBlocks.Pagination;
using FastEndpoints;
using FluentValidation;
namespace Transactions.Features.GetTransactions;

public sealed class GetTransactionsRequestValidator : Validator<GetTransactionsRequest>
{
    public GetTransactionsRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PaginationExtensions.MaxPageSize);
    }
}
