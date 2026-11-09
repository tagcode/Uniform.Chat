namespace Uniform.Chat.Avalonia.Services;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Keeps the homeserver's <see href="https://spec.matrix.org/latest/client-server-api/#push-notifications">pusher</see> of this device in step with <see cref="IPushService"/>: a pusher to the endpoint while registered, none otherwise; and syncs at once when a push message comes.</summary>
/// <remarks>The pusher asks for <c>event_id_only</c>, so neither the gateway nor the distributor sees what was written or by whom; the app syncs to show it.</remarks>
public sealed class PushRegistration : IDisposable
{
    /// <summary>The pusher's application ID, the Android package name.</summary>
    public const string AppId = "rip.uniform.chat";
    /// <summary>The gateway of UnifiedPush, used when the endpoint's server has no Matrix gateway of its own.</summary>
    public const string FallbackGateway = "https://matrix.gateway.unifiedpush.org/_matrix/push/v1/notify";
    /// <summary>Longest wait for the sync a push message starts.</summary>
    private static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(8);
    private readonly ClientConnection connection;
    private readonly IPushService push;
    private readonly string deviceName;
    private readonly HttpClient http;
    private readonly SemaphoreSlim updating = new(1, 1);

    /// <summary>Follow <paramref name="push"/> for <paramref name="connection"/>.</summary>
    /// <param name="connection">The signed-in engine.</param>
    /// <param name="push">The platform's push service.</param>
    /// <param name="deviceName">Device name the pusher shows in other clients.</param>
    /// <param name="handler">HTTP handler for finding the gateway; the default when null.</param>
    public PushRegistration(ClientConnection connection, IPushService push, string deviceName, HttpMessageHandler? handler = null)
    {
        // Follow changes and messages
        this.connection = connection;
        this.push = push;
        this.deviceName = deviceName;
        http = handler is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(10) } : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
        push.Changed += OnChanged;
        push.MessageHandler = WakeAsync;
    }

    /// <summary>Whether push messages wake the app, so it needs no connection of its own in the background.</summary>
    public bool IsActive => push.Endpoint is not null;

    /// <summary>Raised when <see cref="IsActive"/> may have changed.</summary>
    public event Action? ActiveChanged;

    /// <summary>Set or remove the pusher as the push service now stands, e.g. after signing in.</summary>
    public async Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        // One update at a time, as endpoints may change quickly
        await updating.WaitAsync(cancellationToken);
        try
        {
            MatrixClient client = await connection.Session.WhenReady.WaitAsync(cancellationToken);
            string? endpoint = push.Endpoint;

            // Earlier pushers of this app on this device go, unless they are the current one
            foreach (string old in await PushKeysAsync(client, cancellationToken))
            {
                if (old != endpoint) await client.SetPusherAsync(new JsonObject { ["kind"] = null, ["app_id"] = AppId, ["pushkey"] = old }, cancellationToken);
            }
            if (endpoint is null) return;

            // The current one, to the endpoint's gateway
            string gateway = await FindGatewayAsync(endpoint, cancellationToken);
            await client.SetPusherAsync(new
            {
                kind = "http",
                app_id = AppId,
                pushkey = endpoint,
                app_display_name = "Uniform Chat",
                device_display_name = deviceName,
                lang = "en",
                data = new { url = gateway, format = "event_id_only" },
                append = false,
            }, cancellationToken);
            connection.Logger.LogInformation("Push notifications through {Gateway}", gateway);
        }
        catch (Exception exception) when (exception is MatrixException or HttpRequestException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            connection.Logger.LogWarning("Setting the pusher failed: {Error}", exception.Message);
        }
        finally
        {
            updating.Release();
        }
    }

    /// <summary>Return the Matrix gateway of the endpoint's server, as UnifiedPush's <see href="https://unifiedpush.org/developers/gateway/">gateway discovery</see> says: its <c>/_matrix/push/v1/notify</c> when that answers <c>{"unifiedpush":{"gateway":"matrix"}}</c>, else <see cref="FallbackGateway"/>.</summary>
    internal async Task<string> FindGatewayAsync(string endpoint, CancellationToken cancellationToken)
    {
        // The endpoint's own server first
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? url)) return FallbackGateway;
        string own = $"{url.GetLeftPart(UriPartial.Authority)}/_matrix/push/v1/notify";
        try
        {
            JsonElement answer = await http.GetFromJsonAsync<JsonElement>(own, cancellationToken);
            if (answer.TryGetProperty("unifiedpush", out JsonElement unified) && unified.TryGetProperty("gateway", out JsonElement kind) && kind.GetString() == "matrix") return own;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException && !cancellationToken.IsCancellationRequested)
        {
            connection.Logger.LogInformation("No Matrix gateway at {Gateway}: {Error}", own, exception.Message);
        }
        // Return the common one
        return FallbackGateway;
    }

    /// <summary>Stop following the push service.</summary>
    public void Dispose()
    {
        // Unhook
        push.Changed -= OnChanged;
        if (push.MessageHandler == WakeAsync) push.MessageHandler = null;
        http.Dispose();
    }

    /// <summary>Return the push keys of this app's pushers on this account.</summary>
    private static async Task<IReadOnlyList<string>> PushKeysAsync(MatrixClient client, CancellationToken cancellationToken)
    {
        // Pushers with this app ID
        JsonElement pushers = await client.GetPushersAsync(cancellationToken);
        if (pushers.ValueKind != JsonValueKind.Array) return [];
        // Return their keys
        return [.. pushers.EnumerateArray().Where(p => p.TryGetProperty("app_id", out JsonElement app) && app.GetString() == AppId).Select(p => p.GetProperty("pushkey").GetString()).OfType<string>()];
    }

    /// <summary>The endpoint changed: update the pusher, and tell whether push is active.</summary>
    private void OnChanged()
    {
        // In the background
        ActiveChanged?.Invoke();
        _ = UpdateAsync();
    }

    /// <summary>A push message came: sync at once, and wait a little for it, so the platform keeps the app awake meanwhile.</summary>
    private async Task WakeAsync(byte[] message)
    {
        // Interrupt the long poll, then wait for the next applied sync
        if (!connection.Session.WhenReady.IsCompletedSuccessfully) return;
        TaskCompletionSource synced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnApplied(SyncChanges _) => synced.TrySetResult();
        connection.Processor.Applied += OnApplied;
        try
        {
            (await connection.Session.WhenReady).InterruptSync();
            await synced.Task.WaitAsync(WakeTimeout);
        }
        catch (TimeoutException)
        {
            connection.Logger.LogInformation("No sync within {Seconds} s of a push message", WakeTimeout.TotalSeconds);
        }
        finally
        {
            connection.Processor.Applied -= OnApplied;
        }
    }
}
