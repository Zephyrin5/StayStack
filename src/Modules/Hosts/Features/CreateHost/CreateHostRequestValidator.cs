using FastEndpoints;
using FluentValidation;
namespace Hosts.Features.CreateHost;

public sealed class CreateHostRequestValidator : Validator<CreateHostRequest>
{
    public CreateHostRequestValidator()
    {
        RuleFor(x => x.BusinessName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ContactEmail).NotEmpty().EmailAddress().MaximumLength(200);
        RuleFor(x => x.ContactPhone).MaximumLength(50);
    }
}
