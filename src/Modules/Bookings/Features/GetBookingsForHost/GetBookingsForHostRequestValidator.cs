using BuildingBlocks.Pagination;
using FastEndpoints;
using FluentValidation;
namespace Bookings.Features.GetBookingsForHost;

public sealed class GetBookingsForHostRequestValidator : Validator<GetBookingsForHostRequest>
{
    public GetBookingsForHostRequestValidator()
    {
        RuleFor(x => x.HostId).NotEmpty();
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PaginationExtensions.MaxPageSize);
    }
}
