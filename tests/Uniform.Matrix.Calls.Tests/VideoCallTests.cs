namespace Uniform.Matrix.Calls.Tests;

using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.WebRtc;
using Uniform.Matrix.WebRtc.Tests;

/// <summary>Tests of <see cref="VoiceCall"/> with a caller that sends video: Chromium, with this side answering through the call's events; skipped where Chromium is not installed.</summary>
public class VideoCallTests
{
    /// <summary>Chromium's invite with a canvas video rings as a video call; once answered, its pictures come decoded in <see cref="VoiceCall.PictureReceived"/>, and the call logs them when it ends.</summary>
    [Fact]
    public async Task ReceiveCallerVideo()
    {
        // Browser
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Chromium? started;
        try { started = await Chromium.StartAsync(cancellationToken); }
        catch (ChromiumUnavailableException ex) { Assert.Skip(ex.Message); return; }
        await using Chromium? chromium = started;
        if (chromium is null) { Assert.Skip("Chromium is not installed."); return; }

        // Chromium's offer of a tone and a 320x240 canvas, candidates gathered
        string offer = (await chromium.EvaluateAsync("""
            const context = new AudioContext();
            const destination = context.createMediaStreamDestination();
            const canvas = document.createElement('canvas');
            canvas.width = 320;
            canvas.height = 240;
            const draw = canvas.getContext('2d');
            let frame = 0;
            window.painter = setInterval(() => { draw.fillStyle = '#103050'; draw.fillRect(0, 0, 320, 240); draw.fillStyle = '#e0e040'; draw.fillRect((frame * 4) % 280, 100, 40, 40); frame++; }, 33);
            const video = canvas.captureStream(30);
            window.pc = new RTCPeerConnection();
            pc.addTrack(destination.stream.getAudioTracks()[0], destination.stream);
            pc.addTrack(video.getVideoTracks()[0], video);
            await pc.setLocalDescription(await pc.createOffer());
            await new Promise(done => { if (pc.iceGatheringState === 'complete') done(); pc.onicegatheringstatechange = () => pc.iceGatheringState === 'complete' && done(); });
            return pc.localDescription.sdp;
            """, cancellationToken))!.GetValue<string>();

        // Our side rings as a video call
        Channel<(string Type, CallContent Content)> sent = Channel.CreateUnbounded<(string, CallContent)>();
        CallInviteContent invite = new() { CallId = "video1", PartyId = "CHROMIUM", Offer = new CallSessionDescription { Type = "offer", Sdp = offer }, Lifetime = 60000 };
        await using LegacyCall call = new("!room:example.org", "video1", "OURS", "@we:example.org", "@chromium:example.org", invite, (type, content, _) => sent.Writer.WriteAsync((type, content)).AsTask(), () => null, new PeerConnectionOptions { IncludeLoopback = true, ReceiveVideo = true }, NullLogger.Instance);
        Assert.True(call.IsVideo);
        int pictures = 0;
        (int Width, int Height) size = default;
        call.PictureReceived += (_, picture) =>
        {
            size = (picture.Width, picture.Height);
            Interlocked.Increment(ref pictures);
        };

        // Answer; the answer and our candidates to Chromium
        await call.AnswerAsync(cancellationToken);
        CallAnswerContent answer = (CallAnswerContent)(await sent.Reader.ReadAsync(cancellationToken)).Content;
        await chromium.EvaluateAsync($"await pc.setRemoteDescription({{ type: 'answer', sdp: {JsonSerializer.Serialize(answer.Answer.Sdp)} }});", cancellationToken);
        _ = ForwardCandidatesAsync(sent.Reader, chromium, cancellationToken);

        // Pictures of the canvas within a few seconds
        DateTime until = DateTime.UtcNow.AddSeconds(15);
        while (Volatile.Read(ref pictures) < 30 && DateTime.UtcNow < until) await Task.Delay(100, cancellationToken);
        Assert.Equal(CallState.Connected, call.State);
        Assert.True(pictures >= 30, $"{pictures} pictures");
        // Done
        Assert.Equal((320, 240), size);
    }

    /// <summary>Answered with video, the call sends its camera's frames to Chromium, which decodes them, while it still shows Chromium's.</summary>
    [Fact]
    public async Task AnswerWithVideo()
    {
        // Browser
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Chromium? started;
        try { started = await Chromium.StartAsync(cancellationToken); }
        catch (ChromiumUnavailableException ex) { Assert.Skip(ex.Message); return; }
        await using Chromium? chromium = started;
        if (chromium is null) { Assert.Skip("Chromium is not installed."); return; }

        // Chromium sends a canvas and receives video, playing it
        string offer = (await chromium.EvaluateAsync("""
            const context = new AudioContext();
            const destination = context.createMediaStreamDestination();
            const canvas = document.createElement('canvas');
            canvas.width = 320;
            canvas.height = 240;
            const draw = canvas.getContext('2d');
            window.painter = setInterval(() => { draw.fillStyle = '#' + Math.floor(Math.random() * 0xffffff).toString(16).padStart(6, '0'); draw.fillRect(0, 0, 320, 240); }, 33);
            const video = canvas.captureStream(30);
            window.pc = new RTCPeerConnection();
            pc.addTrack(destination.stream.getAudioTracks()[0], destination.stream);
            pc.addTrack(video.getVideoTracks()[0], video);
            pc.ontrack = event => { if (event.track.kind !== 'video') return; window.screen = document.createElement('video'); screen.muted = true; screen.srcObject = new MediaStream([event.track]); document.body.appendChild(screen); screen.play(); };
            await pc.setLocalDescription(await pc.createOffer());
            await new Promise(done => { if (pc.iceGatheringState === 'complete') done(); pc.onicegatheringstatechange = () => pc.iceGatheringState === 'complete' && done(); });
            return pc.localDescription.sdp;
            """, cancellationToken))!.GetValue<string>();

        // Our call with a camera that plays a test vector
        Channel<(string Type, CallContent Content)> sent = Channel.CreateUnbounded<(string, CallContent)>();
        CallInviteContent invite = new() { CallId = "video2", PartyId = "CHROMIUM", Offer = new CallSessionDescription { Type = "offer", Sdp = offer }, Lifetime = 60000 };
        await using FakeCamera camera = new(Path.Combine(AppContext.BaseDirectory, "TestVectors", "vp80-00-comprehensive-017.ivf"));
        await using LegacyCall call = new("!room:example.org", "video2", "OURS", "@we:example.org", "@chromium:example.org", invite, (type, content, _) => sent.Writer.WriteAsync((type, content)).AsTask(), () => null, new PeerConnectionOptions { IncludeLoopback = true, ReceiveVideo = true }, NullLogger.Instance, () => camera);
        int pictures = 0;
        call.PictureReceived += (_, _) => Interlocked.Increment(ref pictures);

        // Answer with video; the answer and candidates to Chromium
        await call.AnswerWithVideoAsync(cancellationToken);
        CallAnswerContent answer = (CallAnswerContent)(await sent.Reader.ReadAsync(cancellationToken)).Content;
        Assert.Contains("a=sendrecv", answer.Answer.Sdp.Split("m=video")[1]);
        await chromium.EvaluateAsync($"await pc.setRemoteDescription({{ type: 'answer', sdp: {JsonSerializer.Serialize(answer.Answer.Sdp)} }});", cancellationToken);
        _ = ForwardCandidatesAsync(sent.Reader, chromium, cancellationToken);

        // Video both ways within a few seconds
        DateTime until = DateTime.UtcNow.AddSeconds(15);
        int decoded = 0;
        while (DateTime.UtcNow < until && (decoded < 30 || Volatile.Read(ref pictures) < 30))
        {
            await Task.Delay(250, cancellationToken);
            decoded = (await chromium.EvaluateAsync("const r = await pc.getStats(); return [...r.values()].find(s => s.type === 'inbound-rtp' && s.kind === 'video')?.framesDecoded ?? 0;", cancellationToken))!.GetValue<int>();
        }
        Assert.True(call.SendsVideo);
        Assert.True(decoded >= 30, $"Chromium decoded {decoded} of our frames");
        // Done
        Assert.True(pictures >= 30, $"{pictures} pictures from Chromium");
    }

    /// <summary>A camera that encodes nothing: it gives the frames of an IVF file at 30 per second, from its keyframe again after its end, and counts keyframe requests.</summary>
    private sealed class FakeCamera(string path) : IVideoCamera
    {
        private readonly CancellationTokenSource stopping = new();
        private Task? running;

        /// <inheritdoc />
        public event Action<byte[]>? FrameEncoded;

        /// <summary>Keyframe requests so far.</summary>
        public int KeyframeRequests { get; private set; }

        /// <inheritdoc />
        public void Start() => running = RunAsync();

        /// <inheritdoc />
        public void RequestKeyframe() => KeyframeRequests++;

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            // Stop the frames
            await stopping.CancelAsync();
            if (running is not null) await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        /// <summary>Give the frames until stopped.</summary>
        private async Task RunAsync()
        {
            // IVF: a header, then size-prefixed frames
            byte[] file = await File.ReadAllBytesAsync(path);
            List<byte[]> frames = [];
            for (int at = BitConverter.ToUInt16(file, 6); at + 12 <= file.Length; at += 12 + BitConverter.ToInt32(file, at)) frames.Add(file[(at + 12)..(at + 12 + BitConverter.ToInt32(file, at))]);
            // Done: paced as a camera
            for (int i = 0; !stopping.IsCancellationRequested; i++)
            {
                FrameEncoded?.Invoke(frames[i % frames.Count]);
                await Task.Delay(33, stopping.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>Give the candidates our call sends to Chromium.</summary>
    private static async Task ForwardCandidatesAsync(ChannelReader<(string Type, CallContent Content)> sent, Chromium chromium, CancellationToken cancellationToken)
    {
        // Candidate events only, empty ones end gathering
        try
        {
            await foreach ((string _, CallContent content) in sent.ReadAllAsync(cancellationToken))
            {
                if (content is not CallCandidatesContent candidates) continue;
                string[] lines = [.. candidates.Candidates.Select(c => c.Candidate).Where(c => c.Length > 0)];
                await chromium.EvaluateAsync($"for (const candidate of {JsonSerializer.Serialize(lines)}) await pc.addIceCandidate({{ candidate, sdpMid: '0', sdpMLineIndex: 0 }});", cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Test over
        }
    }
}
