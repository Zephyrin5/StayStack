namespace SeedWork.Enums;

public enum Currency
{
    // 0 is deliberately not a real currency - default(Currency)/default(Money)
    // (array allocation, a deserializer, an EF materialization edge case on
    // a nullable complex property) must not silently look like a valid "0
    // <some currency>" value. Costs nothing at the database: every
    // configuration persists this via HasConversion<string>(), never the
    // ordinal, confirmed across every site before renumbering.
    None = 0,
    KWD = 1,
    SAR = 2,
    AED = 3,
    USD = 4
}
