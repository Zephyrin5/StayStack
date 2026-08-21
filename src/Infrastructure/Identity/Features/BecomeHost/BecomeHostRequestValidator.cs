using FastEndpoints;
using FluentValidation;
namespace Identity.Features.BecomeHost;

public sealed class BecomeHostRequestValidator : Validator<BecomeHostRequest>
{
    public BecomeHostRequestValidator()
    {
        RuleFor(x => x.BusinessName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ContactEmail).NotEmpty().EmailAddress().MaximumLength(200);
        RuleFor(x => x.ContactPhone).MaximumLength(50);
    }
}
