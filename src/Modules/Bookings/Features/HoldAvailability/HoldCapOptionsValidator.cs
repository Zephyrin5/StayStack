using Microsoft.Extensions.Options;
namespace Bookings.Features.HoldAvailability;

// Source-generated from HoldCapOptions' DataAnnotations, so validation needs no
// reflection (docs/adr/0001).
[OptionsValidator]
internal sealed partial class HoldCapOptionsValidator : IValidateOptions<HoldCapOptions>;
