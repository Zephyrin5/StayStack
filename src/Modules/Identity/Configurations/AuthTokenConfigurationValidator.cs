using Microsoft.Extensions.Options;
namespace Identity.Configurations;

// Source-generated from AuthTokenConfiguration's DataAnnotations, so validation
// needs no reflection (docs/adr/0001).
[OptionsValidator]
internal sealed partial class AuthTokenConfigurationValidator : IValidateOptions<AuthTokenConfiguration>;
