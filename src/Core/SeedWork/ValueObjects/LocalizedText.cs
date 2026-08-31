using Ardalis.GuardClauses;
using System.Collections.ObjectModel;
namespace SeedWork.ValueObjects;

public sealed class LocalizedText : IEquatable<LocalizedText>
{
    private readonly ReadOnlyDictionary<string, string> _values;

    private LocalizedText(IDictionary<string, string> values)
    {
        _values = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(values));
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    public bool Equals(LocalizedText? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (_values.Count != other._values.Count) return false;

        // O(N) allocation-free dictionary equality check
        foreach ((string key, string value) in _values)
        {
            if (!other._values.TryGetValue(key, out string? otherValue) || value != otherValue)
            {
                return false;
            }
        }

        return true;
    }

    public static LocalizedText Create(IDictionary<string, string> values, string requiredLanguageCode)
    {
        Guard.Against.Null(values);
        Guard.Against.NullOrWhiteSpace(requiredLanguageCode);

        if (values.Count == 0)
        {
            throw new ArgumentException("At least one localized value must be provided.", nameof(values));
        }

        if (!values.TryGetValue(requiredLanguageCode, out string? requiredValue) ||
            string.IsNullOrWhiteSpace(requiredValue))
        {
            throw new ArgumentException(
                $"A non-empty value for the required language '{requiredLanguageCode}' must be provided.",
                nameof(values));
        }

        return new LocalizedText(values);
    }

    public static LocalizedText Restore(IDictionary<string, string> values)
    {
        Guard.Against.Null(values);
        return new LocalizedText(values);
    }

    public string GetOrFallback(string languageCode, string fallbackLanguageCode)
    {
        if (_values.TryGetValue(languageCode, out string? val)) return val;
        if (_values.TryGetValue(fallbackLanguageCode, out string? fallbackVal)) return fallbackVal;

        return _values.Values.FirstOrDefault() ?? string.Empty;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as LocalizedText);
    }

    public override int GetHashCode()
    {
        // Order-independent, matching Equals - but not via commutative
        // XOR/add over per-pair combines, which lets different dictionaries
        // collide (combine(a,b)+combine(c,d) can equal combine(a,d)+combine(c,b)).
        // Sorting by key first and feeding one HashCode accumulator avoids
        // that while staying insertion-order independent.
        HashCode hash = new HashCode();
        foreach ((string key, string value) in _values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            hash.Add(key);
            hash.Add(value);
        }
        return hash.ToHashCode();
    }
}
