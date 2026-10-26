namespace Uniform.Matrix.Protocol.Tests;

using System.Text.Json;
using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for <see cref="MatrixEvent.TryGetContent{T}"/>.</summary>
public class EventTests
{
    /// <summary>Deserialize an event from <paramref name="json"/>.</summary>
    private static MatrixEvent Parse(string json) => JsonSerializer.Deserialize<MatrixEvent>(json, MatrixJson.Options)!;

    /// <summary>Well-formed content is read.</summary>
    [Fact]
    public void CanReadContent()
    {
        // Arrange
        MatrixEvent ev = Parse("""{"type":"m.room.topic","content":{"topic":"Hi"}}""");

        // Act and assert
        Assert.True(ev.TryGetContent(out TopicContent? topic));
        Assert.Equal("Hi", topic.Topic);
    }

    /// <summary>Content of the wrong shape or missing content is rejected without throwing.</summary>
    [Theory]
    [InlineData("""{"type":"m.room.topic","content":{"topic":42}}""")]
    [InlineData("""{"type":"m.room.topic","content":"text"}""")]
    [InlineData("""{"type":"m.room.topic"}""")]
    public void CanRejectMalformedContent(string json)
    {
        // Arrange
        MatrixEvent ev = Parse(json);

        // Act and assert
        Assert.False(ev.TryGetContent(out TopicContent? _));
    }
}
