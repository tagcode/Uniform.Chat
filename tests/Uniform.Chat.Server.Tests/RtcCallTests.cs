namespace Uniform.Chat.Server.Tests;

using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Calls;
using Uniform.Matrix.Calls.Tests;
using Uniform.Matrix.Client;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server;

/// <summary>A MatrixRTC call between two clients of the homeserver, through its LiveKit authorization service and LiveKit's own server; run when <c>LIVEKIT_SERVER</c> names its program, skipped otherwise.</summary>
public sealed class RtcCallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const int LiveKitPort = 17890;
    private const int HomeserverPort = 18028;
    private const string ApiKey = "devkey";
    private const string ApiSecret = "a-secret-for-tests-of-matrixrtc-calls";

    /// <summary>Call over MatrixRTC in an encrypted direct chat: the ring, the answer, both members, encrypted voice both ways, a reconnect as after a network change, and the hangup that ends it on both sides.</summary>
    [Fact]
    public async Task CallOverMatrixRtc()
    {
        // LiveKit, and the homeserver with its authorization service
        string program = Environment.GetEnvironmentVariable("LIVEKIT_SERVER") ?? "";
        if (!File.Exists(program)) Assert.Skip("Set LIVEKIT_SERVER to the livekit-server program to run this test.");
        string directory = Directory.CreateTempSubdirectory("uniform-rtc-call-").FullName;
        using Process livekit = await StartLiveKitAsync(program, directory);
        WebApplication server = await StartHomeserverAsync(directory);
        try
        {
            // Alice and Bob in an encrypted direct chat
            Uri address = new($"http://127.0.0.1:{HomeserverPort}/");
            MatrixClient alice = new(new HttpClient { BaseAddress = address }, address);
            await alice.RegisterAsync("alice", "alice-password", cancellationToken: Ct);
            MatrixClient bob = new(new HttpClient { BaseAddress = address }, address);
            await bob.RegisterAsync("bob", "bob-password", cancellationToken: Ct);
            string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.TrustedPrivateChat, IsDirect = true, Invite = ["@bob:test.local"], InitialState = [new InitialStateEvent { Type = EventTypes.RoomEncryption, Content = new { algorithm = "m.megolm.v1.aes-sha2" } }] }, Ct);
            await bob.JoinRoomAsync(roomId, cancellationToken: Ct);

            // Their apps' engines, with voice of their own
            FakeAudio aliceAudio = new(440), bobAudio = new(660);
            using IHost aliceApp = await StartClientAsync(Path.Combine(directory, "alice"), "@alice:test.local", "alice-password", aliceAudio);
            using IHost bobApp = await StartClientAsync(Path.Combine(directory, "bob"), "@bob:test.local", "bob-password", bobAudio);
            CallService aliceCalls = aliceApp.Services.GetRequiredService<CallService>(), bobCalls = bobApp.Services.GetRequiredService<CallService>();
            await WaitForAsync(() => Joined(aliceApp, roomId, 2) && Joined(bobApp, roomId, 2));

            // Alice calls; Bob's rings, and he answers
            TaskCompletionSource<VoiceCall> ringing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bobCalls.IncomingCall += call => ringing.TrySetResult(call);
            VoiceCall outgoing = await aliceCalls.PlaceCallAsync(roomId, Ct);
            Assert.IsType<RtcCall>(outgoing);
            VoiceCall incoming = await ringing.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal("@alice:test.local", incoming.PeerUserId);
            await incoming.AnswerAsync(Ct);

            // Both connected, each hearing the other's voice, decrypted
            await WaitForAsync(() => outgoing.State == CallState.Connected && incoming.State == CallState.Connected);
            await WaitForAsync(() => aliceAudio.Speaker?.Energy > 0 && bobAudio.Speaker?.Energy > 0);

            // Alice's network changes: her call joins the SFU again, and the voice goes on both ways
            await outgoing.RestartIceAsync("test", Ct);
            Assert.Equal(CallState.Connected, outgoing.State);
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            double aliceHeard = aliceAudio.Speaker!.Energy, bobHeard = bobAudio.Speaker!.Energy;
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            Assert.True(aliceAudio.Speaker.Energy > aliceHeard && bobAudio.Speaker.Energy > bobHeard, "Voice stopped after reconnecting.");

            // Bob hangs up; Alice's call ends as his membership goes
            await incoming.HangupAsync(cancellationToken: Ct);
            await WaitForAsync(() => outgoing.State == CallState.Ended);
            // Done
            Assert.True(outgoing.EndedByPeer);
            await aliceApp.StopAsync(CancellationToken.None);
            await bobApp.StopAsync(CancellationToken.None);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            await server.DisposeAsync();
            if (!livekit.HasExited) livekit.Kill();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>Return whether the app has synced <paramref name="roomId"/> with <paramref name="members"/> joined members.</summary>
    private static bool Joined(IHost app, string roomId, int members) => app.Services.GetRequiredService<SyncProcessor>().Read(rooms => rooms?.TryGetRoom(roomId, out Room? room) == true && room.JoinedMemberCount == members);

    /// <summary>Wait until <paramref name="condition"/> holds, at most half a minute.</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        // Polled
        Stopwatch waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("The condition did not hold in time.");
            await Task.Delay(100, Ct);
        }
    }

    /// <summary>Start a client engine of <paramref name="userId"/> with a call service and <paramref name="audio"/>.</summary>
    private static async Task<IHost> StartClientAsync(string directory, string userId, string password, FakeAudio audio)
    {
        // The engine, logged in by its password
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = "Uniform.Chat.Server.Tests", ContentRootPath = AppContext.BaseDirectory, DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Matrix:UserId"] = userId,
            ["Matrix:Homeserver"] = $"http://127.0.0.1:{HomeserverPort}/",
            ["Matrix:Password"] = password,
            ["Matrix:SyncTimeout"] = "00:00:05",
            ["Storage:DataDirectory"] = directory,
        });
        builder.AddMatrixClient();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new TestLoggerProvider(userId));
        builder.Services.AddSingleton<ICredentialPrompt>(new FixedPassword(password));
        builder.Services.AddSingleton<CallService>();
        IHost app = builder.Build();
        app.Services.GetRequiredService<CallService>().Devices = () => audio;
        await app.StartAsync(Ct);
        // Return app
        return app;
    }

    /// <summary>Start the homeserver on a fixed port, its public URL known before it starts, with LiveKit on.</summary>
    private static async Task<WebApplication> StartHomeserverAsync(string directory)
    {
        // Open registration, LiveKit at the test's server
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "Uniform.Chat.Server.Tests", EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:ServerName"] = "test.local",
            ["Server:PublicBaseUrl"] = $"http://127.0.0.1:{HomeserverPort}/",
            ["Server:Registration"] = "Open",
            ["Storage:DataDirectory"] = Path.Combine(directory, "server"),
            ["LiveKit:Enabled"] = "true",
            ["LiveKit:Url"] = $"ws://127.0.0.1:{LiveKitPort}",
            ["LiveKit:ApiKey"] = ApiKey,
            ["LiveKit:ApiSecret"] = ApiSecret,
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{HomeserverPort}");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new TestLoggerProvider("server"));
        builder.AddMatrixServer();
        builder.AddEmbeddedLiveKit();
        WebApplication app = builder.Build();
        app.MapMatrixServer();
        app.MapEmbeddedLiveKit();
        await app.StartAsync(Ct);
        // Return server
        return app;
    }

    /// <summary>Start LiveKit's server on loopback, and return once it answers.</summary>
    private static async Task<Process> StartLiveKitAsync(string program, string directory)
    {
        // Its configuration
        string config = Path.Combine(directory, "livekit.yaml");
        await File.WriteAllTextAsync(config, $"port: {LiveKitPort}\nbind_addresses: [\"127.0.0.1\"]\nrtc:\n  tcp_port: {LiveKitPort + 1}\n  udp_port: {LiveKitPort + 2}\n  use_external_ip: false\nkeys:\n  {ApiKey}: {ApiSecret}\nroom:\n  auto_create: false\n", Ct);
        Process process = Process.Start(new ProcessStartInfo(program, ["--config", config]) { RedirectStandardOutput = true, RedirectStandardError = true }) ?? throw new InvalidOperationException("livekit-server did not start.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Answering
        using HttpClient http = new();
        for (int i = 0; i < 100; i++)
        {
            try
            {
                await http.GetAsync($"http://127.0.0.1:{LiveKitPort}/", Ct);
                return process;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(100, Ct);
            }
        }
        // On a server that did not answer
        process.Kill();
        throw new TimeoutException("livekit-server did not answer.");
    }

    /// <summary>Gives the engine its password.</summary>
    private sealed class FixedPassword(string password) : ICredentialPrompt
    {
        /// <summary>Return the password.</summary>
        public Task<string> GetPasswordAsync(string userId, CancellationToken cancellationToken) => Task.FromResult(password);
    }

    /// <summary>Writes log lines to the test's output, named by their source.</summary>
    private sealed class TestLoggerProvider(string source) : ILoggerProvider
    {
        /// <summary>A logger of the category.</summary>
        public ILogger CreateLogger(string categoryName) => new TestLogger(source, categoryName);

        /// <summary>Nothing to release.</summary>
        public void Dispose() { }
    }

    /// <summary>Writes information and above to the test's output.</summary>
    private sealed class TestLogger(string source, string category) : ILogger
    {
        /// <summary>No scopes.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>Information and above, without the framework's.</summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && !category.StartsWith("Microsoft", StringComparison.Ordinal) && !category.StartsWith("System", StringComparison.Ordinal);

        /// <summary>Write the line.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Done
            if (!IsEnabled(logLevel)) return;
            try { TestContext.Current.TestOutputHelper?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {source} {logLevel}: {formatter(state, exception)}{(exception is null ? "" : " " + exception.Message)}"); }
            catch (InvalidOperationException) { }
        }
    }
}
