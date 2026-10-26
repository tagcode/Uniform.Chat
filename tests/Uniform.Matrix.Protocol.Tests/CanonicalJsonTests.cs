namespace Uniform.Matrix.Protocol.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for <see cref="CanonicalJson"/> with the spec's examples.</summary>
public class CanonicalJsonTests
{
    /// <summary>The <see href="https://spec.matrix.org/latest/appendices/#canonical-json">spec's examples</see> produce the spec's output.</summary>
    [Theory]
    [InlineData("{}", "{}")]
    [InlineData("""{"one": 1, "two": "Two"}""", """{"one":1,"two":"Two"}""")]
    [InlineData("""{"b": "2", "a": "1"}""", """{"a":"1","b":"2"}""")]
    [InlineData("""{"auth": {"success": true, "mxid": "@john.doe:example.com", "profile": {"display_name": "John Doe", "three_pids": [{"medium": "email", "address": "john.doe@example.org"}, {"medium": "msisdn", "address": "123456789"}]}}}""",
        """{"auth":{"mxid":"@john.doe:example.com","profile":{"display_name":"John Doe","three_pids":[{"address":"john.doe@example.org","medium":"email"},{"address":"123456789","medium":"msisdn"}]},"success":true}}""")]
    [InlineData("""{"a": "日本語"}""", """{"a":"日本語"}""")]
    [InlineData("""{"本": 2, "日": 1}""", """{"日":1,"本":2}""")]
    [InlineData("""{"a": "日"}""", """{"a":"日"}""")]
    [InlineData("""{"a": null}""", """{"a":null}""")]
    [InlineData("""{"a": -0, "b": 1e10}""", """{"a":0,"b":10000000000}""")]
    [InlineData("""{"a": "quote \" backslash \\ tab \t"}""", """{"a":"quote \" backslash \\ tab \t"}""")]
    public void CanMatchSpecExamples(string input, string expected)
    {
        // Act and assert
        Assert.Equal(expected, CanonicalJson.Serialize(JsonDocument.Parse(input).RootElement));
    }

    /// <summary>Keys outside the Basic Multilingual Plane sort by code point, after U+FFFF characters.</summary>
    [Fact]
    public void CanSortByCodePoint()
    {
        // Arrange: 😀 is U+1F600, encoded with surrogates below U+FFFD in UTF-16
        JsonElement value = JsonDocument.Parse("""{"😀": 1, "�": 2}""").RootElement;

        // Act and assert
        Assert.Equal("""{"�":2,"😀":1}""".Replace("\\uFFFD", "�"), CanonicalJson.Serialize(value));
    }

    /// <summary>Fractions are not canonical JSON.</summary>
    [Fact]
    public void CanRejectFractions()
    {
        // Act and assert
        Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(JsonDocument.Parse("""{"a": 1.5}""").RootElement));
    }

    /// <summary>Signing form drops signatures and unsigned data; added signatures keep existing ones.</summary>
    [Fact]
    public void CanBuildSigningForm()
    {
        // Arrange
        JsonObject value = JsonNode.Parse("""{"b":1,"unsigned":{"age":5},"signatures":{"@a:x":{"ed25519:A":"old"}}}""")!.AsObject();

        // Act
        string form = CanonicalJson.SigningForm(value);
        CanonicalJson.AddSignature(value, "@a:x", "ed25519:B", "new");

        // Assert
        Assert.Equal("""{"b":1}""", form);
        Assert.Equal("old", CanonicalJson.GetSignature(value, "@a:x", "ed25519:A"));
        Assert.Equal("new", CanonicalJson.GetSignature(value, "@a:x", "ed25519:B"));
    }
}
