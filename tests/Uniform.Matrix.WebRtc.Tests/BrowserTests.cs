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

    /// <summary>Chromium offers audio and 640x480 video drawn on a canvas; we answer receiving the video, put its frames together and decode them with our VP8 decoder; our receiver reports reach it, and a picture loss indication gets a new keyframe.</summary>
    [Fact]
    public async Task ReceiveVideoFromChromium()
    {
        // Browser, and our side receiving video
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Chromium? started;
        try { started = await Chromium.StartAsync(cancellationToken); }
        catch (ChromiumUnavailableException ex) { Assert.Skip(ex.Message); return; }
        await using Chromium? chromium = started;
        if (chromium is null) { Assert.Skip("Chromium is not installed."); return; }
        await using AudioPeerConnection peer = new(new PeerConnectionOptions { IncludeLoopback = true, ReceiveVideo = true });
        Video.Vp8.Vp8Decoder decoder = new();
        List<(int Width, int Height)> pictures = [];
        int keyframes = 0;
        peer.VideoReceived += frame =>
        {
            if (frame.IsKeyframe) Interlocked.Increment(ref keyframes);
            if (decoder.Decode(frame.Data) is Video.VideoFrame picture) lock (pictures) pictures.Add((picture.Width, picture.Height));
        };

        // Chromium's offer: a tone, and a canvas with a moving square
        string offer = (await chromium.EvaluateAsync("""
            const context = new AudioContext();
            const destination = context.createMediaStreamDestination();
            const canvas = document.createElement('canvas');
            canvas.width = 640;
            canvas.height = 480;
            const draw = canvas.getContext('2d');
            let frame = 0;
            window.painter = setInterval(() => { draw.fillStyle = '#204060'; draw.fillRect(0, 0, 640, 480); draw.fillStyle = '#f0c040'; draw.fillRect((frame * 8) % 560, 200, 80, 80); frame++; }, 33);
            const video = canvas.captureStream(30);
            window.pc = new RTCPeerConnection();
            pc.addTrack(destination.stream.getAudioTracks()[0], destination.stream);
            pc.addTrack(video.getVideoTracks()[0], video);
            await pc.setLocalDescription(await pc.createOffer());
            await new Promise(done => { if (pc.iceGatheringState === 'complete') done(); pc.onicegatheringstatechange = () => pc.iceGatheringState === 'complete' && done(); });
            return pc.localDescription.sdp;
            """, cancellationToken))!.GetValue<string>();

        // Our answer accepts the video; both connect
        TaskCompletionSource gathered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.GatheringCompleted += () => gathered.TrySetResult();
        string answer = peer.AcceptOffer(offer);
        Assert.True(peer.ReceivesVideo);
        Assert.Contains("a=recvonly", answer);
        await gathered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        string[] candidates = [.. peer.Ice!.LocalCandidates.Select(c => c.ToString())];
        await chromium.EvaluateAsync($$"""
            await pc.setRemoteDescription({ type: 'answer', sdp: {{JsonSerializer.Serialize(answer)}} });
            for (const candidate of {{JsonSerializer.Serialize(candidates)}}) await pc.addIceCandidate({ candidate, sdpMid: '0', sdpMLineIndex: 0 });
            """, cancellationToken);
        Assert.True(await peer.ConnectAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));

        // Three seconds of video, asking for a keyframe in between
        await Task.Delay(1500, cancellationToken);
        await peer.RequestKeyframeAsync();
        await Task.Delay(1500, cancellationToken);

        // Chromium sent video and got our feedback; we decoded pictures of its size
        JsonNode stats = (await chromium.EvaluateAsync("""
            const report = await pc.getStats();
            const outbound = [...report.values()].find(s => s.type === 'outbound-rtp' && s.kind === 'video');
            const remote = [...report.values()].find(s => s.type === 'remote-inbound-rtp' && s.kind === 'video');
            return { frames: outbound?.framesEncoded ?? 0, keyframes: outbound?.keyFramesEncoded ?? 0, pli: outbound?.pliCount ?? 0, codec: outbound?.encoderImplementation, reports: remote ? 1 : 0, width: outbound?.frameWidth, height: outbound?.frameHeight };
            """, cancellationToken))!;
        int decoded;
        lock (pictures) decoded = pictures.Count;
        Assert.True(decoded > 30, $"{decoded} pictures decoded, {keyframes} keyframes; Chromium: {stats.ToJsonString()}");
        lock (pictures) Assert.All(pictures, p => Assert.Equal((stats["width"]!.GetValue<int>(), stats["height"]!.GetValue<int>()), p));
        Assert.True(stats["pli"]!.GetValue<int>() >= 1, stats.ToJsonString());
        Assert.True(keyframes >= 2, $"{keyframes} keyframes; Chromium: {stats.ToJsonString()}");
        // Done
        Assert.Equal(1, stats["reports"]!.GetValue<int>());
    }

    /// <summary>Chromium offers audio and to receive video; we answer sending VP8 at 30 frames per second: a test vector's frames, as a camera's encoder would give them, or our own encoder's of a moving square; Chromium decodes pictures of their size.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendVideoToChromium(bool ownEncoder)
    {
        // Browser, and our side sending video
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Chromium? started;
        try { started = await Chromium.StartAsync(cancellationToken); }
        catch (ChromiumUnavailableException ex) { Assert.Skip(ex.Message); return; }
        await using Chromium? chromium = started;
        if (chromium is null) { Assert.Skip("Chromium is not installed."); return; }
        await using AudioPeerConnection peer = new(new PeerConnectionOptions { IncludeLoopback = true, SendVideo = true });
        int keyframeRequests = 0;
        peer.KeyframeRequested += () => Interlocked.Increment(ref keyframeRequests);

        // Chromium's offer: a tone, and video to receive into a playing element
        string offer = (await chromium.EvaluateAsync("""
            const context = new AudioContext();
            const destination = context.createMediaStreamDestination();
            window.pc = new RTCPeerConnection();
            pc.addTrack(destination.stream.getAudioTracks()[0], destination.stream);
            pc.addTransceiver('video', { direction: 'recvonly' });
            pc.ontrack = event => { if (event.track.kind !== 'video') return; window.screen = document.createElement('video'); screen.muted = true; screen.srcObject = new MediaStream([event.track]); document.body.appendChild(screen); screen.play(); };
            await pc.setLocalDescription(await pc.createOffer());
            await new Promise(done => { if (pc.iceGatheringState === 'complete') done(); pc.onicegatheringstatechange = () => pc.iceGatheringState === 'complete' && done(); });
            return pc.localDescription.sdp;
            """, cancellationToken))!.GetValue<string>();

        // Our answer sends the video; both connect
        TaskCompletionSource gathered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.GatheringCompleted += () => gathered.TrySetResult();
        string answer = peer.AcceptOffer(offer);
        Assert.True(peer.SendsVideo);
        Assert.False(peer.ReceivesVideo);
        Assert.Contains("a=sendonly", answer);
        await gathered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        string[] candidates = [.. peer.Ice!.LocalCandidates.Select(c => c.ToString())];
        await chromium.EvaluateAsync($$"""
            await pc.setRemoteDescription({ type: 'answer', sdp: {{JsonSerializer.Serialize(answer)}} });
            for (const candidate of {{JsonSerializer.Serialize(candidates)}}) await pc.addIceCandidate({ candidate, sdpMid: '0', sdpMLineIndex: 0 });
            """, cancellationToken);
        Assert.True(await peer.ConnectAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));

        // Three seconds of frames: the vector's, from its keyframe again after its end, or ours
        byte[][] frames = [.. VideoRtpTests.ReadIvf(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestVectors", "vp80-00-comprehensive-017.ivf")))];
        Video.Vp8.Vp8Encoder encoder = new();
        for (int i = 0; i < 90; i++)
        {
            await peer.SendVideoAsync(ownEncoder ? encoder.Encode(Square(i)) : frames[i % frames.Length], cancellationToken);
            await Task.Delay(33, cancellationToken);
        }
        (int width, int height) = ownEncoder ? (320, 240) : (176, 144);

        // Chromium decoded pictures of the vector's size
        JsonNode stats = (await chromium.EvaluateAsync("""
            const report = await pc.getStats();
            const inbound = [...report.values()].find(s => s.type === 'inbound-rtp' && s.kind === 'video');
            return { decoded: inbound?.framesDecoded ?? 0, width: inbound?.frameWidth, height: inbound?.frameHeight, lost: inbound?.packetsLost, pli: inbound?.pliCount ?? 0 };
            """, cancellationToken))!;
        Assert.True(stats["decoded"]!.GetValue<int>() >= 60, stats.ToJsonString());
        Assert.Equal(width, stats["width"]!.GetValue<int>());
        // Done
        Assert.Equal(height, stats["height"]!.GetValue<int>());
    }

    /// <summary>Return a 320x240 picture of frame <paramref name="index"/>: a light square moving over a dark ground.</summary>
    private static Video.VideoFrame Square(int index)
    {
        // Ground, then the square
        byte[] y = new byte[320 * 240], u = new byte[160 * 120], v = new byte[160 * 120];
        Array.Fill(y, (byte)40);
        Array.Fill(u, (byte)128);
        Array.Fill(v, (byte)128);
        int left = index * 4 % 280;
        for (int r = 100; r < 140; r++) y.AsSpan(r * 320 + left, 40).Fill(220);
        // Return picture
        return new Video.VideoFrame(320, 240, y, u, v);
    }
}
