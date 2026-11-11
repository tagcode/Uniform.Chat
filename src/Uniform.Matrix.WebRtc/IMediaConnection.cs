namespace Uniform.Matrix.WebRtc;

using Uniform.Matrix.WebRtc.Rtp;

/// <summary>The media of a call, as its voice and video pipelines see it: Opus frames and VP8 frames each way, with the feedback of the video; a <see cref="AudioPeerConnection"/> to the peer, or the connections of an SFU call together.</summary>
public interface IMediaConnection
{
    /// <summary>Raised for each audio packet received, its payload Opus; on a network thread.</summary>
    event Action<RtpPacket>? AudioReceived;

    /// <summary>Raised for each whole VP8 frame received, in order; on a network thread.</summary>
    event Action<Vp8EncodedFrame>? VideoReceived;

    /// <summary>Raised when the receiver asks to limit the video sent to a bitrate, in bits per second.</summary>
    event Action<long>? BitrateRequested;

    /// <summary>Raised when the receiver asks for a keyframe of the video sent.</summary>
    event Action? KeyframeRequested;

    /// <summary>Whether video may be received, in <see cref="VideoReceived"/>.</summary>
    bool ReceivesVideo { get; }

    /// <summary>Whether video is sent, with <see cref="SendVideoAsync"/>.</summary>
    bool SendsVideo { get; }

    /// <summary>Whether the rotation of sent frames reaches the receiver, with the video orientation extension; when not, frames must be sent upright.</summary>
    bool SendsOrientation { get; }

    /// <summary>Send a frame of Opus, <paramref name="samples"/> long at 48 kHz.</summary>
    ValueTask SendAudioAsync(byte[] opus, int samples = AudioPeerConnection.FrameSamples, CancellationToken cancellationToken = default);

    /// <summary>Skip <paramref name="samples"/> without sending, e.g. while muted, so the timestamps stay in time.</summary>
    void SkipAudio(int samples = AudioPeerConnection.FrameSamples);

    /// <summary>Send a VP8 frame, captured now, shown upright with <paramref name="rotation"/> degrees clockwise.</summary>
    ValueTask SendVideoAsync(byte[] frame, CancellationToken cancellationToken = default, int rotation = 0);

    /// <summary>Ask the sender of the video received for a keyframe.</summary>
    Task RequestKeyframeAsync();

    /// <summary>Ask the sender of the video received to send at most <paramref name="bitsPerSecond"/>.</summary>
    Task RequestBitrateAsync(long bitsPerSecond);
}
