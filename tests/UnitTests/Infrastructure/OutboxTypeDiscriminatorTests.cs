using Bookings.Outbox;
using Outbox;
using System.Reflection;
using Transactions.Outbox;
namespace UnitTests.Infrastructure;

// These strings are persisted into rows that outlive the deployment that wrote
// them, and each dispatcher routes on them. Changing one strands every
// undelivered row of that type - dead-lettered, one per booking or payment it
// was compensating - and nothing about the change says so.
//
// So they are pinned as literals here rather than read back from the constants.
// A test that compares a constant to itself would pass through any edit; this
// one fails, and the failure is the conversation: either add a .v2 alongside
// and keep routing .v1 until no rows carry it, or write the data migration.
public class OutboxTypeDiscriminatorTests
{
    private static readonly (string Declared, string Frozen)[] Identifiers =
    [
        (ReleaseHoldOutboxMessage.TypeName, "bookings.release-hold.v1"),
        (ReverseTransactionOutboxMessage.TypeName, "bookings.reverse-transaction.v1"),
        (ReverseRedemptionOutboxMessage.TypeName, "bookings.reverse-promotion-redemption.v1"),
        (ConfirmBookingPaymentOutboxMessage.TypeName, "transactions.confirm-booking-payment.v1")
    ];

    public static TheoryData<string, string> FrozenIdentifiers
    {
        get
        {
            TheoryData<string, string> data = new TheoryData<string, string>();

            foreach ((string declared, string frozen) in Identifiers)
            {
                data.Add(declared, frozen);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FrozenIdentifiers))]
    public void AnOutboxTypeIdentifier_IsFrozen(string actual, string expected)
    {
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NoTwoMessageTypes_ShareAnIdentifier()
    {
        // A collision routes one type's payload into the other's handler, which
        // deserializes whatever it can and acts on it. Cheap to rule out.
        List<string> identifiers = Identifiers.Select(identifier => identifier.Declared).ToList();

        Assert.Equal(identifiers.Count, identifiers.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryOutboxMessageType_DeclaresAnIdentifier()
    {
        // The list above is hand-written, so it can fall behind. This finds the
        // message type somebody added without pinning it - which is exactly the
        // one that will be renamed later by somebody who never learned the rule.
        List<Type> declared =
        [
            .. new[]
                {
                    typeof(ReleaseHoldOutboxMessage).Assembly,
                    typeof(ConfirmBookingPaymentOutboxMessage).Assembly
                }
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => typeof(IOutboxMessage).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false })
        ];

        // IOutboxMessage is what Enqueue requires, so a type reaching the outbox
        // at all implements it. What this checks is that each one also exposes
        // the const the dispatchers switch on, and that it is in the table above.
        List<string> pinned = Identifiers.Select(identifier => identifier.Declared).ToList();

        foreach (Type type in declared)
        {
            FieldInfo? constant = type.GetField("TypeName", BindingFlags.Public | BindingFlags.Static);

            Assert.True(constant is { IsLiteral: true },
                $"{type.Name} implements IOutboxMessage but has no `public const string TypeName`. " +
                "The dispatchers switch on that constant, and a switch label cannot be a property.");

            Assert.Contains((string)constant!.GetRawConstantValue()!, pinned, StringComparer.Ordinal);
        }
    }
}
