namespace Uniform.Matrix.Protocol.Core;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Shared JSON settings matching Matrix wire conventions.</summary>
public static class MatrixJson
{
    /// <summary>snake_case names, nulls omitted when writing.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
