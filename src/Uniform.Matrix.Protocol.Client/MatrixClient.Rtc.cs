namespace Uniform.Matrix.Protocol.Client;

using System.Net;
using Uniform.Matrix.Protocol.Core;

// MatrixRTC: transports, OpenID tokens and delayed events.
public sealed partial class MatrixClient
{
    /// <summary>The query parameter that delays a send, <see href="https://github.com/matrix-org/matrix-spec-proposals/pull/4140">MSC4140</see>.</summary>
    public const string DelayParameter = "org.matrix.msc4140.delay";

    /// <summary>Return the homeserver's <see href="https://github.com/matrix-org/matrix-spec-proposals/pull/4143">MatrixRTC transports</see>, preferred first; none when the server has none or does not know the endpoint.</summary>
    public async Task<IReadOnlyList<RtcTransport>> GetRtcTransportsAsync(CancellationToken cancellationToken = default)
    {
        // The unstable endpoint
        try
        {
            RtcTransportsResponse response = await SendAsync<RtcTransportsResponse>(HttpMethod.Get, "_matrix/client/unstable/org.matrix.msc4143/rtc/transports", body: null, authenticated: true, cancellationToken);
            return response.RtcTransports;
        }
        catch (MatrixException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed || e.Errcode == ErrorCodes.Unrecognized)
        {
            // On a server without it
            return [];
        }
    }

    /// <summary>Return an <see href="https://spec.matrix.org/latest/client-server-api/#openid">OpenID token</see> of the logged-in user, which another service, e.g. a LiveKit authorization service, verifies with the homeserver.</summary>
    public Task<OpenIdTokenResponse> RequestOpenIdTokenAsync(CancellationToken cancellationToken = default) =>
        SendAsync<OpenIdTokenResponse>(HttpMethod.Post, $"_matrix/client/v3/user/{Escape(UserId ?? throw new InvalidOperationException("Not logged in."))}/openid/request_token", new { }, authenticated: true, cancellationToken);

    /// <summary>Send a state event after <paramref name="delay"/>, unless restarted, cancelled or sent first; return its delay ID. MatrixRTC sends an empty membership so, to leave a call when the device disappears.</summary>
    public async Task<string> SendDelayedStateEventAsync(string roomId, string eventType, object content, string stateKey, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        // The state PUT with the delay
        DelayedEventResponse response = await SendAsync<DelayedEventResponse>(HttpMethod.Put, $"_matrix/client/v3/rooms/{Escape(roomId)}/state/{Escape(eventType)}/{Escape(stateKey)}?{DelayParameter}={(long)delay.TotalMilliseconds}", content, authenticated: true, cancellationToken);
        // Return delay ID
        return response.DelayId;
    }

    /// <summary>Restart, cancel or send a delayed event: <paramref name="action"/> is <c>restart</c>, <c>cancel</c> or <c>send</c>.</summary>
    /// <exception cref="MatrixException">With <see cref="ErrorCodes.NotFound"/> when the delayed event was sent or cancelled already.</exception>
    public async Task UpdateDelayedEventAsync(string delayId, string action, CancellationToken cancellationToken = default)
    {
        // The action in the path, as newer servers take it, else in the body
        try
        {
            await SendAsync(HttpMethod.Post, $"_matrix/client/unstable/org.matrix.msc4140/delayed_events/{Escape(delayId)}/{Escape(action)}", new { }, authenticated: true, cancellationToken);
        }
        catch (MatrixException e) when (e.Errcode == ErrorCodes.Unrecognized || e.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            // Done, the older way
            await SendAsync(HttpMethod.Post, $"_matrix/client/unstable/org.matrix.msc4140/delayed_events/{Escape(delayId)}", new { action }, authenticated: true, cancellationToken);
        }
    }
}
