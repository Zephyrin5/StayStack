using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
namespace BuildingBlocks.Observability;

/// <summary>
///     Redacts [Sensitive]-marked properties from an object for logging and
///     tracing. Deliberately non-generic and standalone: it operates on
///     object, not on any behavior's TMessage/TResponse, so the property
///     cache lives in exactly one place regardless of how many closed
///     generic instantiations of TelemetryPipelineBehavior&lt;,&gt; exist.
/// </summary>
internal static class PayloadRedactor
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new ConcurrentDictionary<Type, PropertyInfo[]>();

    public static string Redact(object? instance)
    {
        if (instance is null) return "null";

        var properties = GetProperties(instance.GetType());
        var parts = new List<string>(properties.Length);

        foreach (PropertyInfo property in properties)
        {
            bool isSensitive = property.GetCustomAttribute<SensitiveAttribute>() is not null;

            string value;
            try
            {
                value = isSensitive
                    ? "[REDACTED]"
                    : property.GetValue(instance)?.ToString() ?? "null";
            }
            catch
            {
                value = "[UNREADABLE]";
            }

            parts.Add($"{property.Name}={value}");
        }

        return string.Join(", ", parts);
    }

    // The one and only place this class calls Type.GetProperties(). Cached
    // per type so repeated Redact() calls for the same message type don't
    // pay the reflection cost twice - and there's exactly one suppression
    // to justify, not one per call site.
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "This app is never published trimmed or Native AOT. GetProperties() " +
                        "is used only for best-effort diagnostic logging of message/response shapes, " +
                        "not for anything that affects program correctness if properties were trimmed.")]
    private static PropertyInfo[] GetProperties(Type type)
    {
        return PropertyCache.GetOrAdd(type, t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance));
    }
}
