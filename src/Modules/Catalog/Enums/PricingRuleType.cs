namespace Catalog.Enums;

// Local to Catalog for the same reason PropertyType.cs is - nothing outside
// this module needs it, and a response DTO carrying it publicly over HTTP
// doesn't by itself justify moving it to SeedWork.
public enum PricingRuleType
{
    DateRangeOverride = 0,
    DayOfWeekMultiplier = 1,
    LengthOfStayDiscount = 2
}
