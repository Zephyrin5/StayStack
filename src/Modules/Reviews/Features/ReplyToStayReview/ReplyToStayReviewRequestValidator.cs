using FastEndpoints;
using FluentValidation;
namespace Reviews.Features.ReplyToStayReview;

public sealed class ReplyToStayReviewRequestValidator : Validator<ReplyToStayReviewRequest>
{
    public ReplyToStayReviewRequestValidator()
    {
        RuleFor(x => x.StayReviewId).NotEmpty();
        RuleFor(x => x.ReplyText).NotEmpty().MaximumLength(2000);
    }
}
