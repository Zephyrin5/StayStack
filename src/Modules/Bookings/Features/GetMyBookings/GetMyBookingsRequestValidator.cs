using BuildingBlocks.Pagination;
using FastEndpoints;
using FluentValidation;
namespace Bookings.Features.GetMyBookings;

public sealed class GetMyBookingsRequestValidator : Validator<GetMyBookingsRequest>
{
    public GetMyBookingsRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PaginationExtensions.MaxPageSize);
    }
}
