using BuildingBlocks.Pagination;
using FastEndpoints;
using FluentValidation;
namespace Identity.Features.GetUsers;

public sealed class GetUsersRequestValidator : Validator<GetUsersRequest>
{
    public GetUsersRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PaginationExtensions.MaxPageSize);
    }
}
