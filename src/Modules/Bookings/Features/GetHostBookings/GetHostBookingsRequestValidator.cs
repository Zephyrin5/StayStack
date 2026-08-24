using BuildingBlocks.Pagination;
using FastEndpoints;
using FluentValidation;
namespace Bookings.Features.GetHostBookings;

public sealed class GetHostBookingsRequestValidator : Validator<GetHostBookingsRequest>
{
    public GetHostBookingsRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PaginationExtensions.MaxPageSize);
    }
}
