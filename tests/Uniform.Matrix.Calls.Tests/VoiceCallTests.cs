namespace Uniform.Matrix.Calls.Tests;

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.WebRtc;

/// <summary>Tests of <see cref="VoiceCall"/>: two calls signalling each other in memory, as the room would carry their events, with media over loopback.</summary>
public class VoiceCallTests
{
    private const string Room = "!room:example.org";
    private const string AliceId = "@alice:example.org";
    private const string BobId = "@bob:example.org";

    /// <summary>Alice calls, Bob answers; each hears the other's tone; Alice hangs up and Bob's call ends by the peer.</summary>
    [Fact]
    public async Task CallAndHangUp()
    {
        // Alice's call, whose invite creates Bob's
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeAudio aliceAudio = new(440), bobAudio = new(660);
        await using Pair pair = new(aliceAudio, bobAudio);
        await pair.Alice.InviteAsync(cancellationToken);
        VoiceCall bob = await pair.Ringing.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(CallState.Ringing, bob.State);
        Assert.Equal(CallState.Inviting, pair.Alice.State);

        // Answered and connected both ways
        await bob.AnswerAsync(cancellationToken);
        await WaitForAsync(() => pair.Alice.State == CallState.Connected && bob.State == CallState.Connected, cancellationToken);
        await WaitForAsync(() => aliceAudio.Speaker?.Energy > 0 && bobAudio.Speaker?.Energy > 0, cancellationToken);

        // Hung up
        await pair.Alice.HangupAsync(cancellationToken: cancellationToken);
        await WaitForAsync(() => bob.State == CallState.Ended, cancellationToken);
        Assert.True(bob.EndedByPeer);
        // Done
        Assert.Equal("user_hangup", bob.EndReason);
    }

    /// <summary>Bob rejects; Alice's call ends as rejected by the peer.</summary>
    [Fact]
    public async Task Reject()
    {
        // Invite, then reject
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using Pair pair = new(null, null);
        await pair.Alice.InviteAsync(cancellationToken);
        VoiceCall bob = await pair.Ringing.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await bob.RejectAsync(cancellationToken);
        await WaitForAsync(() => pair.Alice.State == CallState.Ended, cancellationToken);
        // Done
        Assert.Equal("rejected", pair.Alice.EndReason);
        Assert.True(pair.Alice.EndedByPeer);
    }

    /// <summary>Wait until a condition holds, failing after ten seconds.</summary>
    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        // Poll
        DateTime limit = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > limit) Assert.Fail("Timed out waiting.");
            await Task.Delay(50, cancellationToken);
        }
    }

    /// <summary>Alice's outgoing call, and Bob's incoming one once the invite arrives; events go through queues, in order, as the room carries them.</summary>
    private sealed class Pair : IAsyncDisposable
    {
        private readonly Channel<(string Type, CallContent Content)> toBob = Channel.CreateUnbounded<(string, CallContent)>();
        private readonly Channel<(string Type, CallContent Content)> toAlice = Channel.CreateUnbounded<(string, CallContent)>();
        private readonly CancellationTokenSource stopping = new();
        private readonly Task[] pumps;
        private readonly FakeAudio? bobAudio;
        private VoiceCall? bob;

        /// <summary>Create Alice's call, and start carrying events.</summary>
        public Pair(FakeAudio? aliceAudio, FakeAudio? bobAudio)
        {
            // Loopback media; events into the queues
            this.bobAudio = bobAudio;
            PeerConnectionOptions options = new() { IncludeLoopback = true };
            Alice = new VoiceCall(Room, "call1", "ALICE", AliceId, BobId, null, (type, content, _) => toBob.Writer.WriteAsync((type, content)).AsTask(), () => aliceAudio, options, NullLogger.Instance);
            pumps = [PumpToBobAsync(options), PumpToAliceAsync()];
        }

        /// <summary>Alice's call.</summary>
        public VoiceCall Alice { get; }

        /// <summary>Bob's call, once the invite arrived.</summary>
        public TaskCompletionSource<VoiceCall> Ringing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>End both calls, and stop carrying events.</summary>
        public async ValueTask DisposeAsync()
        {
            // Calls, then the pumps
            await Alice.DisposeAsync();
            if (bob is not null) await bob.DisposeAsync();
            await stopping.CancelAsync();
            await Task.WhenAll(pumps).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        /// <summary>Carry Alice's events to Bob, creating his call from the invite.</summary>
        private async Task PumpToBobAsync(PeerConnectionOptions options)
        {
            // In order
            await foreach ((string type, CallContent content) in toBob.Reader.ReadAllAsync(stopping.Token))
            {
                if (content is CallInviteContent invite)
                {
                    bob = new VoiceCall(Room, invite.CallId, "BOB", BobId, AliceId, invite, (t, c, _) => toAlice.Writer.WriteAsync((t, c)).AsTask(), () => bobAudio, options, NullLogger.Instance);
                    Ringing.TrySetResult(bob);
                    continue;
                }
                await bob!.HandleAsync(AliceId, type, content);
            }
        }

        /// <summary>Carry Bob's events to Alice.</summary>
        private async Task PumpToAliceAsync()
        {
            // In order
            await foreach ((string type, CallContent content) in toAlice.Reader.ReadAllAsync(stopping.Token)) await Alice.HandleAsync(BobId, type, content);
        }
    }
}
