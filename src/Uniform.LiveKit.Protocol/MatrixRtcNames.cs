namespace Uniform.LiveKit.Protocol;

using System.Security.Cryptography;
using System.Text;

/// <summary>LiveKit room names and participant identities of MatrixRTC calls, as lk-jwt-service makes them: SHA-256 of a JSON array of strings, in unpadded Base64, so that they hide the Matrix names and every client and service arrives at the same ones.</summary>
/// <see href="https://github.com/element-hq/lk-jwt-service"/>
public static class MatrixRtcNames
{
    /// <summary>The slot of the room's call, which the legacy <c>/sfu/get</c> request implies.</summary>
    public const string RoomCallSlot = "m.call#ROOM";

    /// <summary>Return the LiveKit room of slot <paramref name="slotId"/> of Matrix room <paramref name="roomId"/>.</summary>
    public static string RoomAlias(string roomId, string slotId) => Hash([roomId, slotId]);

    /// <summary>Return the LiveKit identity of a call member, which <c>/get_token</c> hands out.</summary>
    /// <param name="userId">The member's verified Matrix user.</param>
    /// <param name="deviceId">The device the member claims.</param>
    /// <param name="memberId">The member's id in its membership event.</param>
    public static string Identity(string userId, string deviceId, string memberId) => Hash([userId, deviceId, memberId]);

    /// <summary>Return the LiveKit identity of a member by its user and member id, which newer lk-jwt-service requests from homeservers hand out.</summary>
    public static string MemberIdentity(string userId, string memberId) => Hash([userId, memberId]);

    /// <summary>Return the LiveKit identity of the legacy <c>/sfu/get</c> request, <c>@user:server:DEVICE</c>.</summary>
    public static string LegacyIdentity(string userId, string deviceId) => $"{userId}:{deviceId}";

    /// <summary>Return SHA-256 of <paramref name="values"/> as a compact JSON array, in unpadded standard Base64.</summary>
    public static string Hash(IEnumerable<string> values) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(JsonArray(values)))).TrimEnd('=');

    /// <summary>Return <paramref name="values"/> as a compact JSON array, escaped as Rust's serde_json does: quote, backslash, the short control escapes, and other controls as lowercase <c>\u00xx</c>; everything else as is.</summary>
    public static string JsonArray(IEnumerable<string> values)
    {
        // Strings between commas
        StringBuilder json = new("[");
        bool first = true;
        foreach (string value in values)
        {
            if (!first) json.Append(',');
            first = false;
            json.Append('"');
            foreach (char c in value) Escape(json, c);
            json.Append('"');
        }
        // Return text
        return json.Append(']').ToString();
    }

    /// <summary>Append <paramref name="c"/> escaped as serde_json escapes it.</summary>
    private static void Escape(StringBuilder json, char c)
    {
        // Short escapes, then other controls
        string? escaped = c switch { '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => null };
        if (escaped is not null) json.Append(escaped);
        else if (c < 0x20) json.Append($"\\u{(int)c:x4}");
        // Done
        else json.Append(c);
    }
}
