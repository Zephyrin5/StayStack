using FastEndpoints;
using FluentValidation;
namespace Reviews.Features.CreateGuestReview;

public sealed class CreateGuestReviewRequestValidator : Validator<CreateGuestReviewRequest>
{
    public CreateGuestReviewRequestValidator()
    {
        RuleFor(x => x.BookingId).NotEmpty();
        RuleFor(x => x.OverallRating).InclusiveBetween(1, 5);
        RuleFor(x => x.Comment).MaximumLength(2000);
    }
}
