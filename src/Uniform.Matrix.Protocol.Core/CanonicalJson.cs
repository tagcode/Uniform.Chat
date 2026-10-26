namespace Uniform.Matrix.Protocol.Core;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary><see href="https://spec.matrix.org/latest/appendices/#canonical-json">Canonical JSON</see>: keys sorted by code point, no whitespace, minimal escaping; the form that Matrix signatures are made over.</summary>
public static class CanonicalJson
{
    /// <summary>Serialize <paramref name="value"/> canonically.</summary>
    /// <example><code><![CDATA[CanonicalJson.Serialize(JsonSerializer.SerializeToElement(new { b = 1, a = "x" })); // {"a":"x","b":1}]]></code></example>
    public static string Serialize(JsonElement value)
    {
        // Write recursively
        StringBuilder builder = new();
        Write(builder, value);
        // Return text
        return builder.ToString();
    }

    /// <summary>Serialize an object with <see cref="MatrixJson.Options"/>, then canonically.</summary>
    public static string Serialize(object value) => Serialize(JsonSerializer.SerializeToElement(value, value.GetType(), MatrixJson.Options));

    /// <summary>Return the canonical form signed by <see href="https://spec.matrix.org/latest/appendices/#signing-json">Matrix signatures</see>: without <c>signatures</c> and <c>unsigned</c>.</summary>
    public static string SigningForm(JsonObject value)
    {
        // Copy without signature fields
        JsonObject copy = new();
        foreach ((string name, JsonNode? node) in value)
        {
            if (name is "signatures" or "unsigned") continue;
            copy[name] = node?.DeepClone();
        }
        // Return canonical text
        return Serialize(JsonSerializer.SerializeToElement(copy));
    }

    /// <summary>Add <paramref name="signature"/> as <c>signatures[userId][keyId]</c>, keeping other signatures.</summary>
    public static void AddSignature(JsonObject value, string userId, string keyId, string signature)
    {
        // Find or create the nested maps
        JsonObject signatures = value["signatures"] as JsonObject ?? [];
        value["signatures"] = signatures;
        JsonObject userSignatures = signatures[userId] as JsonObject ?? [];
        signatures[userId] = userSignatures;
        // Set signature
        userSignatures[keyId] = signature;
    }

    /// <summary>Return <c>signatures[userId][keyId]</c>, or null.</summary>
    public static string? GetSignature(JsonObject value, string userId, string keyId) => value["signatures"]?[userId]?[keyId]?.GetValue<string>();

    /// <summary>Write one value.</summary>
    private static void Write(StringBuilder builder, JsonElement value)
    {
        // By kind
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                bool first = true;
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(p => p.Name, CodePointComparer.Instance))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    WriteString(builder, property.Name);
                    builder.Append(':');
                    Write(builder, property.Value);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                bool firstItem = true;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (!firstItem) builder.Append(',');
                    firstItem = false;
                    Write(builder, item);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(builder, value.GetString()!);
                break;
            case JsonValueKind.Number:
                // Canonical JSON only allows integers, written plainly even when sent as 1e10
                builder.Append(ToInteger(value).ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    /// <summary>Return a JSON number as an integer; throw for fractions and values outside the safe integer range.</summary>
    private static long ToInteger(JsonElement value)
    {
        // Plain integers
        if (value.TryGetInt64(out long number)) return number;
        // Exponent forms such as 1e10, when whole and exactly representable
        const double MaxSafe = 9007199254740991;
        if (value.TryGetDouble(out double real) && real == Math.Floor(real) && Math.Abs(real) <= MaxSafe) return (long)real;
        // Return nothing for invalid numbers
        throw new ArgumentException($"Canonical JSON numbers must be integers: {value.GetRawText()}");
    }

    /// <summary>Write a string escaping only quote, backslash and control characters; other characters stay as UTF-8.</summary>
    private static void WriteString(StringBuilder builder, string value)
    {
        // Escape by character
        builder.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case < ' ': builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: builder.Append(c); break;
            }
        }
        builder.Append('"');
    }

    /// <summary>Orders strings by Unicode code point, which differs from UTF-16 ordinal order for characters outside the Basic Multilingual Plane.</summary>
    private sealed class CodePointComparer : IComparer<string>
    {
        /// <summary>Shared instance.</summary>
        public static readonly CodePointComparer Instance = new();

        /// <summary>Compare rune by rune.</summary>
        public int Compare(string? x, string? y)
        {
            // Nulls first
            if (x is null || y is null) return x is null ? (y is null ? 0 : -1) : 1;

            // First differing rune decides
            StringRuneEnumerator left = x.EnumerateRunes();
            StringRuneEnumerator right = y.EnumerateRunes();
            while (true)
            {
                bool hasLeft = left.MoveNext();
                bool hasRight = right.MoveNext();
                if (!hasLeft || !hasRight) return hasLeft ? 1 : hasRight ? -1 : 0;
                int order = left.Current.Value.CompareTo(right.Current.Value);
                if (order != 0) return order;
            }
        }
    }
}
