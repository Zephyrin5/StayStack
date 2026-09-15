using Api.RateLimiting;
using Bookings.Contracts;
using BuildingBlocks.Localization;
using Catalog.Contracts;
using Microsoft.Extensions.Options;
namespace Api.Configuration;

// Source-generated from each options type's DataAnnotations, so validation needs
// no reflection (docs/adr/0001). Registered beside each type's Bind in
// Program.cs and ApiServicesRegistration.

[OptionsValidator]
internal sealed partial class BookingLifecyclePolicyOptionsValidator : IValidateOptions<BookingLifecyclePolicyOptions>;

[OptionsValidator]
internal sealed partial class StaySearchPolicyOptionsValidator : IValidateOptions<StaySearchPolicyOptions>;

[OptionsValidator]
internal sealed partial class AuthRateLimitOptionsValidator : IValidateOptions<AuthRateLimitOptions>;

[OptionsValidator]
internal sealed partial class HoldRateLimitOptionsValidator : IValidateOptions<HoldRateLimitOptions>;

[OptionsValidator]
internal sealed partial class ReadRateLimitOptionsValidator : IValidateOptions<ReadRateLimitOptions>;

// The per-field rules. The cross-field rule is LocalizationSettingsValidator.
[OptionsValidator]
internal sealed partial class LocalizationSettingsAttributesValidator : IValidateOptions<LocalizationSettings>;
