namespace BuildingBlocks.Observability;

/// <summary>
///     Marks a property on a command or response as sensitive. The telemetry
///     middleware redacts any property carrying this attribute before it is
///     written to a trace span, a log line, or any metric tag. Apply this to
///     passwords, tokens, refresh tokens, and anything else that must never
///     leave the process in plaintext observability output.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveAttribute : Attribute;
