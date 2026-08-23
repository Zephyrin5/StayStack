using FastEndpoints;
using FluentValidation;
namespace Identity.Features.SignUp;

public sealed class SignUpRequestValidator : Validator<SignUpRequest>
{
    public SignUpRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress();

        // Matches IdentityServicesRegistration's Password options exactly -
        // length + unique-char count over composition rules, current NIST
        // guidance rather than forced symbols/digits. Whether the email is
        // already registered is a database concern, checked in the
        // handler, not duplicated here.
        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(12);

        RuleFor(x => x.ConfirmPassword)
            .Equal(x => x.Password)
            .WithMessage("Passwords do not match.");
    }
}
