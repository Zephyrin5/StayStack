using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
namespace BuildingBlocks.Observability;

/// <summary>
///     Redacts [Sensitive]-marked properties from an object for logging and
///     tracing. The generic message type lets the trimmer preserve exactly
///     the public properties this diagnostic formatter inspects.
/// </summary>
internal static class PayloadRedactor
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new ConcurrentDictionary<Type, PropertyInfo[]>();

    public static string Redact<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
        TMessage>(TMessage? instance)
    {
        if (instance is null) return "null";

        var properties = GetProperties<TMessage>();
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

    // The generic parameter carries the preservation requirement to typeof(TMessage),
    // while the shared cache still avoids repeatedly enumerating a message type.
    private static PropertyInfo[] GetProperties<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
        TMessage>()
    {
        Type type = typeof(TMessage);

        if (PropertyCache.TryGetValue(type, out var properties))
        {
            return properties;
        }

        properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        return PropertyCache.GetOrAdd(type, properties);
    }
}
