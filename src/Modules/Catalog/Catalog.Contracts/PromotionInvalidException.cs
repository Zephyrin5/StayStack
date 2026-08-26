using BuildingBlocks.Exceptions;
using System.Net;
namespace Catalog.Contracts;

/// <summary>
///     A promo code can't be redeemed for the specific reason carried in
///     message - unknown code, expired, wrong host, wrong currency,
///     redemption cap reached, or already used by this guest email. Thrown
///     by IPromotionRedemption.RedeemAsync, and lives in this Contracts
///     project (not Catalog.Exceptions) rather than in the main Catalog
///     project specifically so Bookings - which references Catalog.Contracts
///     but not Catalog itself - can catch it. ConfirmBookingHandler catches
///     this and re-throws it as a field-keyed ValidationException
///     (nameof(request.PromoCode), ...), since a flat AppException message
///     doesn't carry the field key the client needs to show the error
///     against the promo-code input specifically rather than as a generic
///     failure.
/// </summary>
public sealed class PromotionInvalidException(string message)
    : AppException(message, (int)HttpStatusCode.BadRequest);
