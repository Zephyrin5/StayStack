namespace BuildingBlocks.Persistence;

/// <summary>
///     Marks a method whose minting SS0002 should allow, with the reason why a retry does not need to
///     recognise what it mints.
///     <para>
///         The bar: a retry that produces a different value must lose nothing. An access token's
///         <c>jti</c> qualifies - tokens are stateless and never looked up, so two are equally valid.
///         An entity id never does (docs/adr/0025).
///     </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor)]
public sealed class AllowsMintingInRetryAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
