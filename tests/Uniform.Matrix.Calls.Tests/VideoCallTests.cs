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
        await using VoiceCall call = new("!room:example.org", "video1", "OURS", "@we:example.org", "@chromium:example.org", invite, (type, content, _) => sent.Writer.WriteAsync((type, content)).AsTask(), () => null, new PeerConnectionOptions { IncludeLoopback = true, ReceiveVideo = true }, NullLogger.Instance);
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
