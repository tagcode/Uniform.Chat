namespace Uniform.Matrix.WebRtc.Tests;

using System.Threading.Channels;
using Uniform.Matrix.WebRtc.Rtp;

/// <summary>Tests of <see cref="AudioPeerConnection"/> between two connections on this machine.</summary>
public class PeerConnectionTests
{
    /// <summary>Offer, answer, trickle candidates, connect, and send audio both ways.</summary>
    [Fact]
    public async Task CallOnThisMachine()
    {
        // Caller and callee, candidates trickled as gathered
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PeerConnectionOptions options = new() { IncludeLoopback = true };
        await using AudioPeerConnection caller = new(options), callee = new(options);
        Channel<RtpPacket> toCaller = Channel.CreateUnbounded<RtpPacket>(), toCallee = Channel.CreateUnbounded<RtpPacket>();
        caller.AudioReceived += packet => toCaller.Writer.TryWrite(packet);
        callee.AudioReceived += packet => toCallee.Writer.TryWrite(packet);
        caller.CandidateGathered += candidate => callee.AddRemoteCandidate(candidate.ToString());
        callee.CandidateGathered += candidate => caller.AddRemoteCandidate(candidate.ToString());

        // Signalling, then both connect
        string offer = caller.CreateOffer();
        string answer = callee.AcceptOffer(offer);
        caller.AcceptAnswer(answer);
        bool[] connected = await Task.WhenAll(caller.ConnectAsync(cancellationToken), callee.ConnectAsync(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.All(connected, Assert.True);
        Assert.Equal(PeerConnectionState.Connected, caller.State);

        // Frames each way, in order, the first marked
        for (byte i = 0; i < 3; i++) await caller.SendAudioAsync([i, 1], cancellationToken: cancellationToken);
        for (byte i = 0; i < 3; i++)
        {
            RtpPacket packet = await toCallee.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(new byte[] { i, 1 }, packet.Payload);
            Assert.Equal(i == 0, packet.Marker);
        }
        await callee.SendAudioAsync([9], cancellationToken: cancellationToken);
        // Done
        Assert.Equal(new byte[] { 9 }, (await toCaller.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)).Payload);
    }

    /// <summary>Restart ICE during a call, as after a network change: new agents on both sides, the same DTLS and SRTP, and audio both ways after it.</summary>
    [Fact]
    public async Task RestartIce()
    {
        // A call on this machine, candidates trickled as gathered
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PeerConnectionOptions options = new() { IncludeLoopback = true };
        await using AudioPeerConnection caller = new(options), callee = new(options);
        Channel<RtpPacket> toCaller = Channel.CreateUnbounded<RtpPacket>(), toCallee = Channel.CreateUnbounded<RtpPacket>();
        caller.AudioReceived += packet => toCaller.Writer.TryWrite(packet);
        callee.AudioReceived += packet => toCallee.Writer.TryWrite(packet);
        caller.CandidateGathered += candidate => callee.AddRemoteCandidate(candidate.ToString());
        callee.CandidateGathered += candidate => caller.AddRemoteCandidate(candidate.ToString());
        caller.AcceptAnswer(callee.AcceptOffer(caller.CreateOffer()));
        Assert.All(await Task.WhenAll(caller.ConnectAsync(cancellationToken), callee.ConnectAsync(cancellationToken)), Assert.True);
        Uniform.Matrix.WebRtc.Ice.IceAgent before = caller.Ice!;

        // Restart from the callee's side of the offer, both connect again
        string offer = callee.CreateRestartOffer();
        Assert.True(callee.IsRestarting);
        Assert.Contains("a=setup:actpass", offer);
        string answer = caller.AcceptRestartOffer(offer);
        Assert.Contains("a=setup:passive", answer);
        callee.AcceptRestartAnswer(answer);
        bool[] reconnected = await Task.WhenAll(caller.ReconnectAsync(cancellationToken), callee.ReconnectAsync(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.All(reconnected, Assert.True);
        Assert.NotSame(before, caller.Ice);
        Assert.False(callee.IsRestarting);

        // Audio both ways over the new agents
        await caller.SendAudioAsync([7], cancellationToken: cancellationToken);
        Assert.Equal(new byte[] { 7 }, (await toCallee.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)).Payload);
        await callee.SendAudioAsync([8], cancellationToken: cancellationToken);
        // Done
        Assert.Equal(new byte[] { 8 }, (await toCaller.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)).Payload);
    }
}
