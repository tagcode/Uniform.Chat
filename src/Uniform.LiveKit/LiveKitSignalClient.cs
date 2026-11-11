namespace Uniform.LiveKit;

using System.Net.WebSockets;
using Google.Protobuf;
using global::LiveKit.Proto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>LiveKit's signalling: a WebSocket to the server's <c>/rtc</c>, with protobuf <see cref="SignalRequest"/> messages out and <see cref="SignalResponse"/> messages in, and the pings that keep it open.</summary>
/// <remarks>It joins with two peer connections, as LiveKit's clients did before the single one: the server offers the subscriber's, and this side offers the publisher's.</remarks>
/// <see href="https://docs.livekit.io/reference/internals/client-protocol/"/>
public sealed class LiveKitSignalClient : IAsyncDisposable
{
    /// <summary>The signalling protocol this client speaks: the server's offers and answers, and trickled candidates, of two peer connections.</summary>
    public const int ProtocolVersion = 16;
    private readonly ClientWebSocket socket;
    private readonly ILogger logger;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim sending = new(1, 1);
    private Task? receiving;
    private Task? pinging;

    /// <summary>Create a client over an open socket.</summary>
    private LiveKitSignalClient(ClientWebSocket socket, JoinResponse join, ILogger logger)
    {
        // The socket and what the server said
        this.socket = socket;
        Join = join;
        this.logger = logger;
    }

    /// <summary>What the server said on joining: the room, this participant, the others, and the ICE servers.</summary>
    public JoinResponse Join { get; }

    /// <summary>Raised for each message of the server after the join, on the receiving loop.</summary>
    public event Action<SignalResponse>? MessageReceived;

    /// <summary>Raised once when the socket closed, by either side or by failing; on the receiving loop.</summary>
    public event Action? Closed;

    /// <summary>The round trip of the last ping, as the server's pong measured it.</summary>
    public TimeSpan RoundTrip { get; private set; }

    /// <summary>Return the URL of the server's signalling for <paramref name="url"/>, the server's <c>ws://</c> or <c>wss://</c> URL, with the token and this client's options.</summary>
    public static Uri RtcUrl(string url, string token, bool autoSubscribe = true)
    {
        // ws or wss, at /rtc
        UriBuilder builder = new(url.TrimEnd('/') + "/rtc");
        if (builder.Scheme == "https") builder.Scheme = "wss";
        if (builder.Scheme == "http") builder.Scheme = "ws";
        builder.Port = builder.Uri.IsDefaultPort ? -1 : builder.Port;
        builder.Query = $"access_token={Uri.EscapeDataString(token)}&auto_subscribe={(autoSubscribe ? 1 : 0)}&sdk=go&version=0.1.0&protocol={ProtocolVersion}&adaptive_stream=0";
        // Return URL
        return builder.Uri;
    }

    /// <summary>Connect to the server at <paramref name="url"/> with <paramref name="token"/>, and return the client once the server answered the join; it reads the messages after it from <see cref="Start"/> on, so that none is missed.</summary>
    /// <exception cref="WebSocketException">When the server cannot be reached, or refuses the token.</exception>
    /// <exception cref="InvalidDataException">When the server's first message is not a join, e.g. a leave.</exception>
    public static async Task<LiveKitSignalClient> ConnectAsync(string url, string token, ILogger? logger = null, bool autoSubscribe = true, CancellationToken cancellationToken = default)
    {
        // The socket, and the join as its first message
        ClientWebSocket socket = new();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await socket.ConnectAsync(RtcUrl(url, token, autoSubscribe), cancellationToken);
            SignalResponse first = await ReceiveAsync(socket, cancellationToken) ?? throw new InvalidDataException("LiveKit closed before joining.");
            if (first.MessageCase != SignalResponse.MessageOneofCase.Join) throw new InvalidDataException($"LiveKit answered the join with {first.MessageCase}.");
            // Return client
            return new LiveKitSignalClient(socket, first.Join, logger ?? NullLogger.Instance);
        }
        catch
        {
            // On a failed join
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Start reading the server's messages into <see cref="MessageReceived"/>, and pinging; once, after the handlers are added.</summary>
    public void Start()
    {
        // Once
        if (receiving is not null) return;
        receiving = ReceiveLoopAsync();
        // Done
        pinging = PingLoopAsync();
    }

    /// <summary>Send a message to the server; nothing after closing.</summary>
    public async Task SendAsync(SignalRequest request, CancellationToken cancellationToken = default)
    {
        // One message at a time, as the socket wants
        if (socket.State != WebSocketState.Open) return;
        byte[] bytes = request.ToByteArray();
        await sending.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
        {
            // On a socket that closed meanwhile
            logger.LogDebug(e, "LiveKit signal message not sent");
        }
        finally
        {
            sending.Release();
        }
    }

    /// <summary>Tell the server this participant leaves, and close the socket.</summary>
    public async ValueTask DisposeAsync()
    {
        // Leave, then close; the loops end
        if (stopping.IsCancellationRequested) return;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            await SendAsync(new SignalRequest { Leave = new LeaveRequest { Reason = DisconnectReason.ClientInitiated, Action = LeaveRequest.Types.Action.Disconnect } }, timeout.Token);
            if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "leave", timeout.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // On a socket already gone
        }
        await stopping.CancelAsync();
        if (receiving is not null) await receiving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (pinging is not null) await pinging.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        // Done
        socket.Dispose();
    }

    /// <summary>Read messages until the socket closes, then tell it.</summary>
    private async Task ReceiveLoopAsync()
    {
        // Each message to the listeners; a listener's failure is logged, not fatal
        try
        {
            while (await ReceiveAsync(socket, stopping.Token) is SignalResponse response)
            {
                if (response.MessageCase == SignalResponse.MessageOneofCase.PongResp) RoundTrip = TimeSpan.FromMilliseconds(Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - response.PongResp.LastPingTimestamp));
                try { MessageReceived?.Invoke(response); }
                catch (Exception e) { logger.LogWarning(e, "LiveKit signal message {Message} failed", response.MessageCase); }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidProtocolBufferException or ObjectDisposedException)
        {
            // On a socket that broke or closed
            if (!stopping.IsCancellationRequested) logger.LogInformation("LiveKit signalling ended: {Error}", e.Message);
        }
        // Done
        Closed?.Invoke();
    }

    /// <summary>Ping at the interval the server asked, with the last round trip, so the server keeps this participant.</summary>
    private async Task PingLoopAsync()
    {
        // Every interval until closed
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(Join.PingInterval > 0 ? Join.PingInterval : 5));
        while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
        {
            await SendAsync(new SignalRequest { PingReq = new Ping { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Rtt = (long)RoundTrip.TotalMilliseconds } }, stopping.Token);
        }
    }

    /// <summary>Return the next whole message of the socket, or null when it closed.</summary>
    private static async Task<SignalResponse?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        // Frames until the end of a message
        using MemoryStream message = new();
        byte[] buffer = new byte[16384];
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        // Return message
        return SignalResponse.Parser.ParseFrom(message.ToArray());
    }
}
