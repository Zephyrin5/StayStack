using FastEndpoints;
using FluentValidation;
namespace Reviews.Features.CreateStayReview;

public sealed class CreateStayReviewRequestValidator : Validator<CreateStayReviewRequest>
{
    public CreateStayReviewRequestValidator()
    {
        RuleFor(x => x.BookingId).NotEmpty();
        RuleFor(x => x.CleanlinessRating).InclusiveBetween(1, 5);
        RuleFor(x => x.CommunicationRating).InclusiveBetween(1, 5);
        RuleFor(x => x.LocationRating).InclusiveBetween(1, 5);
        RuleFor(x => x.ValueRating).InclusiveBetween(1, 5);
        RuleFor(x => x.AccuracyRating).InclusiveBetween(1, 5);
        RuleFor(x => x.Comment).MaximumLength(2000);

        // Whether BookingId refers to a real, owned, eligible booking, and
        // whether it's already been reviewed, are database/cross-module
        // concerns checked in the handler - same convention as every other
        // request validator in this app.
    }
}
