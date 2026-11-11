namespace Uniform.LiveKit.Tests;

using System.Text;
using System.Text.Json;
using Uniform.LiveKit.Protocol;

/// <summary>Tests of access tokens and MatrixRTC names.</summary>
public sealed class ProtocolTests
{
    /// <summary>Sign a token and read it back; a wrong secret, a changed body or an expired token does not read.</summary>
    [Fact]
    public void SignAndRead()
    {
        // A token of an hour
        DateTimeOffset now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        AccessToken claims = AccessToken.Create("key", "alice", new VideoGrant { RoomJoin = true, Room = "room", CanPublish = false }, now, TimeSpan.FromHours(1));
        string jwt = claims.Sign("secret");

        // Read back as signed
        Assert.True(AccessToken.TryRead(jwt, "secret", now.AddMinutes(1), out AccessToken? read));
        Assert.Equal(claims, read);
        Assert.False(AccessToken.TryRead(jwt, "other", now, out _));
        Assert.False(AccessToken.TryRead(jwt, "secret", now.AddHours(2), out _));
        string[] parts = jwt.Split('.');
        string forged = $"{parts[0]}.{Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"iss":"key","sub":"mallory","nbf":0,"exp":9999999999}""")).TrimEnd('=')}.{parts[2]}";
        Assert.False(AccessToken.TryRead(forged, "secret", now, out _));
    }

    /// <summary>Write the claims with LiveKit's names, and leave unset permissions out.</summary>
    [Fact]
    public void WriteClaimNames()
    {
        // The body of a token
        string jwt = AccessToken.Create("key", "alice", new VideoGrant { RoomJoin = true, Room = "room", CanPublish = true, CanSubscribe = true }, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60)).Sign("secret");
        using JsonDocument claims = JsonDocument.Parse(Decode(jwt.Split('.')[1]));

        // Done
        Assert.Equal("""{"iss":"key","sub":"alice","nbf":0,"exp":60,"video":{"roomJoin":true,"room":"room","canPublish":true,"canSubscribe":true}}""", claims.RootElement.GetRawText());
        Assert.Equal("""{"alg":"HS256","typ":"JWT"}""", Encoding.UTF8.GetString(Decode(jwt.Split('.')[0])));
    }

    /// <summary>Return the bytes of unpadded URL-safe Base64.</summary>
    private static byte[] Decode(string text)
    {
        // Standard alphabet, padded
        string standard = text.Replace('-', '+').Replace('_', '/');
        // Return bytes
        return Convert.FromBase64String(standard + new string('=', (4 - standard.Length % 4) % 4));
    }

    /// <summary>Hash room aliases and identities as lk-jwt-service does; the expected values come from Python's compact JSON, which escapes as serde_json does.</summary>
    [Fact]
    public void HashNames()
    {
        // Plain names
        Assert.Equal("9YzbCxM8P0fwObUtiQ9m/ujMP9ruNMxS7SOsugBEaxA", MatrixRtcNames.RoomAlias("!abc:example.org", MatrixRtcNames.RoomCallSlot));
        Assert.Equal("hGMIOq+Ruz2K+rhsvbbNAvweTdRBfh8U/rqCYC6i5CE", MatrixRtcNames.Identity("@alice:example.org", "DEVICE", "member1"));
        Assert.Equal("@alice:example.org:DEVICE", MatrixRtcNames.LegacyIdentity("@alice:example.org", "DEVICE"));

        // Escapes, and other characters as they are
        Assert.Equal("[\"!ä\\\"\\\\\\n\\u0001:example.org\"]", MatrixRtcNames.JsonArray(["!ä\"\\\n\u0001:example.org"]));
        // Done
        Assert.Equal("Ht1nJK6QQzchC03IQRfvy0MkjzpiyX+dPUkHZ6n511g", MatrixRtcNames.RoomAlias("!ä\"\\\n\u0001:example.org", MatrixRtcNames.RoomCallSlot));
    }
}
