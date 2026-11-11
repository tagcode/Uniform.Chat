namespace Uniform.LiveKit;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Uniform.LiveKit.Protocol;

/// <summary>Calls LiveKit's room service over Twirp with JSON, authorized by a short access token signed with the API secret.</summary>
/// <see href="https://docs.livekit.io/reference/server/server-apis/"/>
/// <param name="http">The client of the requests.</param>
/// <param name="url">LiveKit's URL, <c>wss://</c> or <c>https://</c>; WebSocket schemes become HTTP ones.</param>
/// <param name="apiKey">The API key.</param>
/// <param name="apiSecret">The API secret.</param>
/// <param name="time">Clock of the tokens.</param>
public sealed class LiveKitRoomService(HttpClient http, string url, string apiKey, string apiSecret, TimeProvider? time = null)
{
    /// <summary>Lifetime of the tokens of these calls.</summary>
    private static readonly TimeSpan CallTokenLifetime = TimeSpan.FromMinutes(1);

    /// <summary>Return LiveKit's HTTP base URL for <paramref name="url"/>: <c>ws</c> as <c>http</c>, <c>wss</c> as <c>https</c>, without a trailing slash.</summary>
    public static string HttpUrl(string url)
    {
        // Scheme swapped, slash trimmed
        string trimmed = url.TrimEnd('/');
        if (trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) return "https://" + trimmed[6..];
        if (trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) return "http://" + trimmed[5..];
        // Return as is
        return trimmed;
    }

    /// <summary>Create room <paramref name="name"/>, or keep it as it is when it exists.</summary>
    /// <param name="name">The room name.</param>
    /// <param name="emptyTimeout">How long the room waits for its first participant.</param>
    /// <param name="departureTimeout">How long the room stays after the last participant leaves.</param>
    /// <param name="maxParticipants">Most participants, 0 for no limit.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task CreateRoomAsync(string name, TimeSpan emptyTimeout, TimeSpan departureTimeout, int maxParticipants = 0, CancellationToken cancellationToken = default)
    {
        // Proto3 JSON names
        JsonObject body = new() { ["name"] = name, ["empty_timeout"] = (long)emptyTimeout.TotalSeconds, ["departure_timeout"] = (long)departureTimeout.TotalSeconds, ["max_participants"] = maxParticipants };
        // Done
        await CallAsync("CreateRoom", body, new VideoGrant { RoomCreate = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Call <paramref name="method"/> of the room service with <paramref name="grant"/>; return the answer, or throw <see cref="HttpRequestException"/> with LiveKit's reason.</summary>
    private async Task<JsonNode?> CallAsync(string method, JsonObject body, VideoGrant grant, CancellationToken cancellationToken)
    {
        // A short token for this call only
        string token = AccessToken.Create(apiKey, null, grant, (time ?? TimeProvider.System).GetUtcNow(), CallTokenLifetime).Sign(apiSecret);
        using HttpRequestMessage request = new(HttpMethod.Post, $"{HttpUrl(url)}/twirp/livekit.RoomService/{method}") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Twirp errors are JSON with a code and a message
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"LiveKit {method} failed: {(int)response.StatusCode} {text}", null, response.StatusCode);
        // Return answer
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }
}
