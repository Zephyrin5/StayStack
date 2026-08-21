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

        // DisplayName's own content validation (non-empty values, at least
        // one entry) is deliberately left to LocalizedText.Create's guard
        // clauses in the handler, not duplicated here - same reasoning as
        // HoldAvailabilityRequestValidator not re-checking what the
        // handler already validates.
    }
}
