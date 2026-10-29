namespace Uniform.Matrix.WebRtc.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Audio;
using Uniform.Matrix.WebRtc.Rtp;

/// <summary>Calls between Chromium's WebRTC and <see cref="AudioPeerConnection"/>; skipped where Chromium is not installed.</summary>
public class BrowserTests
{
    /// <summary>Chromium offers a 440 Hz tone; we answer and send a tone back; each side must hear the other.</summary>
    [Fact]
    public async Task CallChromium()
    {
        // Browser, and our side
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Chromium? started;
        try { started = await Chromium.StartAsync(cancellationToken); }
        catch (ChromiumUnavailableException ex) { Assert.Skip(ex.Message); return; }
        await using Chromium? chromium = started;
        if (chromium is null) { Assert.Skip("Chromium is not installed."); return; }
        await using AudioPeerConnection peer = new(new PeerConnectionOptions { IncludeLoopback = true });
        VoiceDecoder decoder = new();
        double energy = 0;
        int packets = 0;
        peer.AudioReceived += packet =>
        {
            short[] frame = decoder.Decode(packet.Payload);
            Interlocked.Increment(ref packets);
            energy += frame.Sum(s => (double)s * s) / frame.Length;
        };

        // Chromium's offer with a tone, all candidates gathered
        string offer = (await chromium.EvaluateAsync("""
            const context = new AudioContext();
            const tone = context.createOscillator();
            tone.frequency.value = 440;
            const destination = context.createMediaStreamDestination();
            tone.connect(destination);
            tone.start();
            window.pc = new RTCPeerConnection();
            pc.ontrack = event => { window.player = new Audio(); player.srcObject = event.streams[0] ?? new MediaStream([event.track]); player.play(); };
            pc.addTrack(destination.stream.getAudioTracks()[0], destination.stream);
            await pc.setLocalDescription(await pc.createOffer());
            await new Promise(done => { if (pc.iceGatheringState === 'complete') done(); pc.onicegatheringstatechange = () => pc.iceGatheringState === 'complete' && done(); });
            return pc.localDescription.sdp;
            """, cancellationToken))!.GetValue<string>();

        // Our answer and candidates to Chromium, then both connect
        TaskCompletionSource gathered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.GatheringCompleted += () => gathered.TrySetResult();
        string answer = peer.AcceptOffer(offer);
        await gathered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        string[] candidates = [.. peer.Ice!.LocalCandidates.Select(c => c.ToString())];
        await chromium.EvaluateAsync($$"""
            await pc.setRemoteDescription({ type: 'answer', sdp: {{JsonSerializer.Serialize(answer)}} });
            for (const candidate of {{JsonSerializer.Serialize(candidates)}}) await pc.addIceCandidate({ candidate, sdpMid: '0', sdpMLineIndex: 0 });
            """, cancellationToken);
        Assert.True(await peer.ConnectAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));

        // Two seconds of our tone, paced as a microphone would
        VoiceEncoder encoder = new();
        for (int frame = 0; frame < 100; frame++)
        {
            short[] samples = new short[VoiceFormat.FrameSamples];
            for (int i = 0; i < samples.Length; i++) samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (frame * samples.Length + i) / VoiceFormat.SampleRate));
            await peer.SendAudioAsync(encoder.Encode(samples), cancellationToken: cancellationToken);
            await Task.Delay(VoiceFormat.FrameMilliseconds, cancellationToken);
        }

        // Chromium heard us: packets decoded with energy
        JsonNode stats = (await chromium.EvaluateAsync("""
            const report = await pc.getStats();
            const inbound = [...report.values()].find(s => s.type === 'inbound-rtp' && s.kind === 'audio');
            return { packets: inbound?.packetsReceived ?? 0, energy: inbound?.totalAudioEnergy ?? 0, state: pc.connectionState, samples: inbound?.totalSamplesReceived, concealed: inbound?.concealedSamples, level: inbound?.audioLevel, decoder: inbound?.decoderImplementation, lost: inbound?.packetsLost };
            """, cancellationToken))!;
        Assert.Equal("connected", stats["state"]!.GetValue<string>());
        Assert.True(stats["packets"]!.GetValue<int>() > 50, stats.ToJsonString());
        Assert.True(stats["energy"]!.GetValue<double>() > 0, stats.ToJsonString());
        // Done
        Assert.True(packets > 50, $"{packets} packets from Chromium");
        Assert.True(energy > 0, "Chromium's tone was silent.");
    }
}
