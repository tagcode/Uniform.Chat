namespace Uniform.Matrix.Protocol.Server.Tests;

using System.Text.Json.Nodes;

/// <summary>Event hashing, signing, IDs and redaction against the spec's <see href="https://spec.matrix.org/latest/appendices/#cryptographic-test-vectors">cryptographic test vectors</see>.</summary>
public class EventFormatTests
{
    private const string Seed = "YJDBA9Xnr2sVqXD9Vj7XVUnmFZcZrlw8Md7kMW+3XA1";

    /// <summary>The minimal event of the spec gets its hash and signature.</summary>
    [Fact]
    public void SignMinimalEvent()
    {
        // Arrange
        JsonObject pdu = JsonNode.Parse("""
            {"room_id":"!x:domain","sender":"@a:domain","origin":"domain","origin_server_ts":1000000,"signatures":{},"hashes":{},"type":"X","content":{},"prev_events":[],"auth_events":[],"depth":3,"unsigned":{"age_ts":1000000}}
            """)!.AsObject();

        // Act
        EventFormat.HashAndSign(pdu, RoomVersion.V10, "domain", "ed25519:1", Seed);

        // Assert
        Assert.Equal("5jM4wQpv6lnBo7CLIghJuHdW+s2CMBJPUOGOC89ncos", pdu["hashes"]!["sha256"]!.GetValue<string>());
        Assert.Equal("KxwGjPSDEtvnFgU00fwFz+l6d2pJM6XBIaMEn81SXPTRl16AqLAYqfIReFGZlHi5KLjAWbOoMszkwsQma+lYAg", pdu["signatures"]!["domain"]!["ed25519:1"]!.GetValue<string>());
        Assert.True(EventFormat.HasValidContentHash(pdu));
    }

    /// <summary>The spec's message event is signed over its redacted form, so the body does not change the signature but does change the hash.</summary>
    [Fact]
    public void SignRedactableEvent()
    {
        // Arrange
        JsonObject pdu = JsonNode.Parse("""
            {"content":{"body":"Here is the message content"},"event_id":"$0:domain","origin":"domain","origin_server_ts":1000000,"type":"m.room.message","room_id":"!r:domain","sender":"@u:domain","signatures":{},"unsigned":{"age_ts":1000000}}
            """)!.AsObject();

        // Act
        EventFormat.HashAndSign(pdu, RoomVersion.V10, "domain", "ed25519:1", Seed);

        // Assert
        Assert.Equal("onLKD1bGljeBWQhWZ1kaP9SorVmRQNdN5aM2JYU2n/g", pdu["hashes"]!["sha256"]!.GetValue<string>());
        Assert.Equal("Wm+VzmOUOz08Ds+0NTWb1d4CZrVsJSikkeRxh6aCcUwu6pNC78FunoD7KNWzqFn241eYHYMGCA5McEiVPdhzBA", pdu["signatures"]!["domain"]!["ed25519:1"]!.GetValue<string>());
        string publicKey = Crypto.Ed25519.GetPublicKey(Seed);
        Assert.True(EventFormat.HasValidSignature(pdu, RoomVersion.V10, "domain", "ed25519:1", publicKey));
        pdu["content"]!["body"] = "changed";
        Assert.True(EventFormat.HasValidSignature(pdu, RoomVersion.V10, "domain", "ed25519:1", publicKey));
        Assert.False(EventFormat.HasValidContentHash(pdu));
    }

    /// <summary>Event IDs are URL-safe reference hashes that ignore signatures and unsigned data but cover the content hash.</summary>
    [Fact]
    public void ComputeEventIds()
    {
        // Arrange
        JsonObject pdu = JsonNode.Parse("""{"type":"m.room.message","room_id":"!r:domain","sender":"@u:domain","origin_server_ts":1,"depth":2,"prev_events":[],"auth_events":[],"content":{"body":"a"}}""")!.AsObject();
        EventFormat.HashAndSign(pdu, RoomVersion.V11, "domain", "ed25519:1", Seed);

        // Act
        string id = EventFormat.ComputeEventId(pdu, RoomVersion.V11);
        JsonObject resigned = pdu.DeepClone().AsObject();
        resigned["signatures"] = new JsonObject();
        resigned["unsigned"] = new JsonObject { ["age"] = 5 };
        JsonObject edited = pdu.DeepClone().AsObject();
        edited["content"]!["body"] = "b";
        EventFormat.HashAndSign(edited, RoomVersion.V11, "domain", "ed25519:1", Seed);

        // Assert
        Assert.StartsWith("$", id);
        Assert.Equal(44, id.Length);
        Assert.DoesNotContain('+', id);
        Assert.DoesNotContain('/', id);
        Assert.Equal(id, EventFormat.ComputeEventId(resigned, RoomVersion.V11));
        Assert.NotEqual(id, EventFormat.ComputeEventId(edited, RoomVersion.V11));
    }

    /// <summary>Redaction keeps what each room version protects.</summary>
    [Fact]
    public void RedactByVersion()
    {
        // Arrange
        JsonObject create = JsonNode.Parse("""{"type":"m.room.create","origin":"domain","content":{"creator":"@a:domain","room_version":"11","m.federate":true}}""")!.AsObject();
        JsonObject levels = JsonNode.Parse("""{"type":"m.room.power_levels","content":{"ban":50,"invite":0,"notifications":{"room":50},"users":{"@a:domain":100}}}""")!.AsObject();
        JsonObject member = JsonNode.Parse("""{"type":"m.room.member","membership":"join","content":{"membership":"join","displayname":"A","third_party_invite":{"signed":{"token":"t"},"display_name":"x"}}}""")!.AsObject();
        JsonObject message = JsonNode.Parse("""{"type":"m.room.redaction","content":{"redacts":"$e","reason":"spam"}}""")!.AsObject();

        // Act
        JsonObject create10 = EventFormat.Redact(create, RoomVersion.V10), create11 = EventFormat.Redact(create, RoomVersion.V11);
        JsonObject levels10 = EventFormat.Redact(levels, RoomVersion.V10), levels11 = EventFormat.Redact(levels, RoomVersion.V11);
        JsonObject member10 = EventFormat.Redact(member, RoomVersion.V10), member11 = EventFormat.Redact(member, RoomVersion.V11);

        // Assert
        Assert.Equal("""{"creator":"@a:domain"}""", create10["content"]!.ToJsonString());
        Assert.Equal(3, create11["content"]!.AsObject().Count);
        Assert.Equal("domain", create10["origin"]!.GetValue<string>());
        Assert.False(create11.ContainsKey("origin"));
        Assert.False(levels10["content"]!.AsObject().ContainsKey("invite"));
        Assert.True(levels11["content"]!.AsObject().ContainsKey("invite"));
        Assert.False(levels11["content"]!.AsObject().ContainsKey("notifications"));
        Assert.Equal("""{"membership":"join"}""", member10["content"]!.ToJsonString());
        Assert.Equal("""{"membership":"join","third_party_invite":{"signed":{"token":"t"}}}""", member11["content"]!.ToJsonString());
        Assert.True(member10.ContainsKey("membership"));
        Assert.Equal("""{}""", EventFormat.Redact(message, RoomVersion.V10)["content"]!.ToJsonString());
        Assert.Equal("""{"redacts":"$e"}""", EventFormat.Redact(message, RoomVersion.V11)["content"]!.ToJsonString());
    }
}
