namespace Uniform.LiveKit.Authorization;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

/// <summary>Acts on delayed events through the client-server API of the user's homeserver, found from the server name by its <c>/.well-known/matrix/client</c>, never from a URL the client gave, so the service calls only real homeservers.</summary>
/// <param name="http">The client of the requests.</param>
public sealed class HomeserverDelayedEvents(HttpClient http) : IDelayedEvents
{
    private const string Path = "_matrix/client/unstable/org.matrix.msc4140/delayed_events";
    private readonly ConcurrentDictionary<string, string> baseUrls = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public async Task<bool> UpdateAsync(string serverName, string delayId, string action, CancellationToken cancellationToken)
    {
        // The action in the path, as newer servers take it, else in the body
        string baseUrl = await BaseUrlAsync(serverName, cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage response = await http.PostAsJsonAsync($"{baseUrl}/{Path}/{Uri.EscapeDataString(delayId)}/{Uri.EscapeDataString(action)}", new { }, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound && !await IsMissingAsync(response, cancellationToken).ConfigureAwait(false))
        {
            using HttpResponseMessage older = await http.PostAsJsonAsync($"{baseUrl}/{Path}/{Uri.EscapeDataString(delayId)}", new { action }, cancellationToken).ConfigureAwait(false);
            return await ReadAsync(older, cancellationToken).ConfigureAwait(false);
        }
        // Return whether it was there
        return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Return true for success and false for a delayed event the homeserver no longer has; throw otherwise.</summary>
    private static async Task<bool> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Done, gone, or failed
        if (response.IsSuccessStatusCode) return true;
        if (await IsMissingAsync(response, cancellationToken).ConfigureAwait(false)) return false;
        throw new HttpRequestException($"The homeserver answered {(int)response.StatusCode}.", null, response.StatusCode);
    }

    /// <summary>Return whether the answer says the delayed event is not there, M_NOT_FOUND, rather than that the endpoint is unknown.</summary>
    private static async Task<bool> IsMissingAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Matrix's error code
        if (response.StatusCode != HttpStatusCode.NotFound) return false;
        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))?["errcode"]?.GetValue<string>() == "M_NOT_FOUND";
        }
        catch (System.Text.Json.JsonException)
        {
            // Done: not Matrix's answer
            return false;
        }
    }

    /// <summary>Return the client-server API base URL of <paramref name="serverName"/>: its well-known one, else <c>https://</c> and the name; remembered.</summary>
    private async Task<string> BaseUrlAsync(string serverName, CancellationToken cancellationToken)
    {
        // Remembered, else asked
        if (baseUrls.TryGetValue(serverName, out string? known)) return known;
        string found = $"https://{serverName}";
        try
        {
            JsonNode? wellKnown = await http.GetFromJsonAsync<JsonNode>($"https://{serverName}/.well-known/matrix/client", cancellationToken).ConfigureAwait(false);
            if (wellKnown?["m.homeserver"]?["base_url"]?.GetValue<string>() is string url && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) && parsed.Scheme == Uri.UriSchemeHttps) found = url.TrimEnd('/');
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // On no well-known: the name itself
        }
        // Return URL
        return baseUrls[serverName] = found;
    }
}
