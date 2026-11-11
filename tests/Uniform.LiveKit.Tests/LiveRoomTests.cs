namespace Uniform.LiveKit.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using global::LiveKit.Proto;
using Uniform.LiveKit.Protocol;
using Uniform.Matrix.Video;
using Uniform.Matrix.Video.Vp8;
using Uniform.Matrix.WebRtc;
using Uniform.Matrix.WebRtc.Rtp;

/// <summary>Tests of <see cref="LiveKitRoom"/> against LiveKit's own server, run when <c>LIVEKIT_SERVER</c> names its program; skipped otherwise.</summary>
public sealed class LiveRoomTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string ApiKey = "devkey";
    private const string ApiSecret = "a-secret-for-tests-of-livekit-rooms-only";
    private const int Port = 17880;
    private static readonly string Url = $"ws://127.0.0.1:{Port}";

    /// <summary>Publish encrypted audio and video from one participant, and receive and decrypt them in another, as Element Call's members would.</summary>
    [Fact]
    public async Task PublishAndSubscribe()
    {
        // LiveKit, and the call's room
        await using LiveKitServer server = await LiveKitServer.StartAsync();
        string room = MatrixRtcNames.RoomAlias("!room:example.org", MatrixRtcNames.RoomCallSlot);
        await new LiveKitRoomService(new HttpClient(), Url, ApiKey, ApiSecret, TimeProvider.System).CreateRoomAsync(room, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), 0, Ct);
        LiveKitRoomOptions options = new() { IncludeLoopback = true };

        // Alice publishes, encrypting with her key; Bob knows it
        byte[] aliceKey = RandomNumberGenerator.GetBytes(16);
        FrameCryptor encrypting = new();
        encrypting.SetKey(0, aliceKey);
        FrameCryptor decrypting = new();
        decrypting.SetKey(0, aliceKey);
        await using LiveKitRoom alice = await LiveKitRoom.ConnectAsync(Url, Token("@alice:example.org:ALICE", room, publish: true), options, new TestLogger(), Ct);
        AudioPeerConnection publisher = await alice.PublishAsync(video: true, Encryption.Types.Type.Gcm, 64, 48, Ct);
        await using LiveKitRoom bob = await LiveKitRoom.ConnectAsync(Url, Token("@bob:example.org:BOB", room, publish: false), options, new TestLogger(), Ct);
        Assert.Contains(bob.Participants, p => p.Identity == "@alice:example.org:ALICE");

        // What Bob decrypts, and from whom
        ConcurrentQueue<byte[]> heard = new(), seen = new();
        ConcurrentQueue<string?> senders = new();
        void Listen(AudioPeerConnection subscriber)
        {
            subscriber.AudioReceived += packet => { senders.Enqueue(bob.IdentityOf(packet.Ssrc)); if (decrypting.Decrypt(packet.Payload, FrameKind.Audio) is byte[] plain) heard.Enqueue(plain); };
            subscriber.VideoReceived += frame => { if (decrypting.Decrypt(frame.Data, FrameKind.Vp8) is byte[] plain) seen.Enqueue(plain); };
        }
        if (bob.Subscriber is AudioPeerConnection existing) Listen(existing);
        else bob.SubscriberCreated += Listen;

        // Alice talks and shows herself for a few seconds
        Vp8Encoder encoder = new() { KeyframeInterval = 10 };
        HashSet<string> sentAudio = [], sentVideo = [];
        Stopwatch clock = Stopwatch.StartNew();
        for (uint i = 0; clock.Elapsed < TimeSpan.FromSeconds(6) && (heard.IsEmpty || seen.IsEmpty); i++)
        {
            byte[] opus = [0xF8, .. RandomNumberGenerator.GetBytes(40)];
            sentAudio.Add(Convert.ToHexString(opus));
            await publisher.SendAudioAsync(encrypting.Encrypt(opus, FrameKind.Audio, 1, i), cancellationToken: Ct);
            if (i % 5 == 0)
            {
                byte[] frame = encoder.Encode(Picture((byte)(i * 7)));
                sentVideo.Add(Convert.ToHexString(frame));
                await publisher.SendVideoAsync(encrypting.Encrypt(frame, FrameKind.Vp8, 2, i), Ct);
            }
            await Task.Delay(20, Ct);
        }

        // Bob got Alice's frames as she sent them, and knew they were hers
        Assert.NotEmpty(heard);
        Assert.All(heard, plain => Assert.Contains(Convert.ToHexString(plain), sentAudio));
        Assert.NotEmpty(seen);
        Assert.All(seen, plain => Assert.Contains(Convert.ToHexString(plain), sentVideo));
        // Done
        Assert.Contains("@alice:example.org:ALICE", senders);
    }

    /// <summary>Return a LiveKit token of <paramref name="identity"/> to <paramref name="room"/>.</summary>
    private static string Token(string identity, string room, bool publish) => AccessToken.Create(ApiKey, identity, new VideoGrant { RoomJoin = true, Room = room, CanPublish = publish, CanSubscribe = true, CanPublishData = publish }, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10)).Sign(ApiSecret);

    /// <summary>Return a small picture of one shade.</summary>
    private static VideoFrame Picture(byte shade) => new(64, 48, Enumerable.Repeat(shade, 64 * 48).ToArray(), Enumerable.Repeat((byte)128, 32 * 24).ToArray(), Enumerable.Repeat((byte)128, 32 * 24).ToArray());

    /// <summary>LiveKit's server on this machine, on loopback, for one test.</summary>
    private sealed class LiveKitServer : IAsyncDisposable
    {
        private readonly Process process;
        private readonly string directory;

        /// <summary>Wrap a started server.</summary>
        private LiveKitServer(Process process, string directory)
        {
            // Done
            this.process = process;
            this.directory = directory;
        }

        /// <summary>Start the server <c>LIVEKIT_SERVER</c> names, and return once it answers; skip the test without one.</summary>
        public static async Task<LiveKitServer> StartAsync()
        {
            // The program, and a configuration of its own
            string? program = Environment.GetEnvironmentVariable("LIVEKIT_SERVER");
            if (string.IsNullOrEmpty(program) || !File.Exists(program)) Assert.Skip("Set LIVEKIT_SERVER to the livekit-server program to run this test.");
            string directory = Directory.CreateTempSubdirectory("uniform-livekit-").FullName;
            string config = Path.Combine(directory, "livekit.yaml");
            await File.WriteAllTextAsync(config, $"port: {Port}\nbind_addresses: [\"127.0.0.1\"]\nrtc:\n  tcp_port: {Port + 1}\n  udp_port: {Port + 2}\n  use_external_ip: false\nkeys:\n  {ApiKey}: {ApiSecret}\nroom:\n  auto_create: false\nlogging:\n  level: debug\n", Ct);
            Process process = Process.Start(new ProcessStartInfo(program, ["--config", config]) { RedirectStandardOutput = true, RedirectStandardError = true }) ?? throw new InvalidOperationException("livekit-server did not start.");
            StreamWriter? log = Environment.GetEnvironmentVariable("LIVEKIT_LOG") is { Length: > 0 } path ? new StreamWriter(path) { AutoFlush = true } : null;
            process.OutputDataReceived += (_, line) => { lock (process) log?.WriteLine(line.Data); };
            process.ErrorDataReceived += (_, line) => { lock (process) log?.WriteLine(line.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            LiveKitServer server = new(process, directory);

            // Answering
            using HttpClient http = new();
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    await http.GetAsync($"http://127.0.0.1:{Port}/", Ct);
                    return server;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(100, Ct);
                }
            }
            // On a server that did not answer
            await server.DisposeAsync();
            throw new TimeoutException("livekit-server did not answer.");
        }

        /// <summary>Stop the server, and remove its configuration.</summary>
        public async ValueTask DisposeAsync()
        {
            // Done
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            process.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}

/// <summary>Writes log lines to the test's output.</summary>
internal sealed class TestLogger : Microsoft.Extensions.Logging.ILogger
{
    /// <summary>No scopes.</summary>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>Every level.</summary>
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    /// <summary>Write the line, with its exception.</summary>
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => TestContext.Current.TestOutputHelper?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {logLevel}: {formatter(state, exception)} {exception}");
}
