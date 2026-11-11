---
uid: Uniform.Matrix.Calls.Article
title: Uniform.Matrix.Calls
---
# Uniform.Matrix.Calls

Matrix 1:1 voice and video calls for the [client engine](xref:Uniform.Matrix.Client.Article), of two kinds:

- [`RtcCall`](xref:Uniform.Matrix.Calls.RtcCall): [MatrixRTC](https://github.com/matrix-org/matrix-spec-proposals/pull/4143) through a LiveKit SFU, as Element X and Element Call make calls, over [Uniform.LiveKit](xref:Uniform.LiveKit.Article).
- [`LegacyCall`](xref:Uniform.Matrix.Calls.LegacyCall): the [`m.call.*` events](https://spec.matrix.org/latest/client-server-api/#voice-over-ip) of version 1, directly between the apps, as Element on the desktop and Element Classic make calls.

Both are a [`VoiceCall`](xref:Uniform.Matrix.Calls.VoiceCall), which the apps show the same way. Media goes over [Uniform.Matrix.WebRtc](xref:Uniform.Matrix.WebRtc.Article), voice through [Uniform.Matrix.Audio](xref:Uniform.Matrix.Audio.Article), and video is decoded with [Uniform.Matrix.Video](xref:Uniform.Matrix.Video.Article).

## Service

[`CallService`](xref:Uniform.Matrix.Calls.CallService) is a singleton beside the client engine. It follows [`SyncProcessor.Applied`](xref:Uniform.Matrix.Client.Services.SyncProcessor.Applied), routes each call event to its call, and raises [`IncomingCall`](xref:Uniform.Matrix.Calls.CallService.IncomingCall) for a fresh invite or ring to this user. It places MatrixRTC calls when the homeserver has a LiveKit transport, unless [`UseMatrixRtc`](xref:Uniform.Matrix.Calls.CallService.UseMatrixRtc) is off, and legacy calls otherwise. Events go out through the [`OutboxSender`](xref:Uniform.Matrix.Client.Services.OutboxSender), which encrypts them in encrypted rooms, as Element does. TURN credentials come from the homeserver's `/voip/turnServer`, and are kept until shortly before they expire.

To add calls to an app, register the service, and set the devices when the app chooses them:

```csharp
builder.Services.AddSingleton<CallService>();
// …
calls.Devices = () => AudioDevices.ForThisPlatform(microphone, speaker);
calls.IncomingCall += call => ShowRinging(call);
VoiceCall call = await calls.PlaceCallAsync(roomId);
```

## A MatrixRTC call

1. **Joining.** An OpenID token buys a LiveKit token from the homeserver's authorization service at `/sfu/get`, so this side's identity on the SFU is `@user:server:DEVICE`. The call publishes on the SFU and writes its membership, the state event `org.matrix.msc3401.call.member` with state key `_@user:server_DEVICE_m.call`. A delayed event of empty content ([MSC4140](https://github.com/matrix-org/matrix-spec-proposals/pull/4140)) ends the membership 18 seconds after the device stops restarting it, every 4 seconds.
2. **Ringing.** The caller sends `org.matrix.msc4075.rtc.notification` with `ring`, referring to its membership. The callee rings while the caller is in the call, until it answers by joining, or declines with `org.matrix.msc4310.rtc.decline`.
3. **Connected.** Once the peer's membership is there, each side sends its media key in `io.element.call.encryption_keys` to the peer's call devices over Olm, through [`CryptoService.SendEncryptedToDevicesAsync`](xref:Uniform.Matrix.Client.Services.CryptoService.SendEncryptedToDevicesAsync*). Keys come back in [`ToDeviceReceived`](xref:Uniform.Matrix.Client.Services.CryptoService.ToDeviceReceived). In encrypted rooms, frames are encrypted as Element Call encrypts them. A peer on another homeserver is subscribed to on its own SFU, with a token of its service's `/get_token`.
4. **Ended.** Hanging up sends the delayed leave at once and leaves the SFUs. The call ends when the peer's membership goes, or on a decline or a timeout.

## A legacy call

A [`LegacyCall`](xref:Uniform.Matrix.Calls.LegacyCall):

1. **Ringing or inviting.** The caller sends `m.call.invite` with the offer, and the callee rings until it answers, rejects, or the invite's lifetime passes.
2. **Connecting.** The callee answers with `m.call.answer`; the caller picks the first answer with `m.call.select_answer`, so the user's other devices stop ringing. Candidates go both ways in `m.call.candidates` batches, ending with an empty candidate; those that arrive before the offer or answer wait for it.
3. **Connected.** [`VoiceMedia`](xref:Uniform.Matrix.Calls.VoiceMedia) sends the microphone as Opus, and plays what arrives through a jitter buffer, paced by the speaker. [`Muted`](xref:Uniform.Matrix.Calls.VoiceCall.Muted) stops sending.
   When the caller sends video, [`IsVideo`](xref:Uniform.Matrix.Calls.VoiceCall.IsVideo) is set while it rings, and the answer accepts the video, unless [`CallService.ReceiveVideo`](xref:Uniform.Matrix.Calls.CallService.ReceiveVideo) is off. [`VideoReceiver`](xref:Uniform.Matrix.Calls.VideoReceiver) decodes it on its own task and the pictures come in [`PictureReceived`](xref:Uniform.Matrix.Calls.VoiceCall.PictureReceived). When decoding falls behind or a frame does not decode, frames are skipped up to a keyframe, which the peer is asked for. [`AnswerWithVideoAsync`](xref:Uniform.Matrix.Calls.VoiceCall.AnswerWithVideoAsync*) also sends this side's camera, from the platform's [`IVideoCamera`](xref:Uniform.Matrix.Calls.IVideoCamera) in [`CallService.Camera`](xref:Uniform.Matrix.Calls.CallService.Camera): its encoded VP8 frames go to the peer once connected, and the peer's keyframe requests go to its encoder. [`PlaceVideoCallAsync`](xref:Uniform.Matrix.Calls.CallService.PlaceVideoCallAsync*) calls with video. The camera's rotation goes with its frames, and its small [`Previewed`](xref:Uniform.Matrix.Calls.IVideoCamera.Previewed) pictures come in [`PreviewReceived`](xref:Uniform.Matrix.Calls.VoiceCall.PreviewReceived); [`CameraOff`](xref:Uniform.Matrix.Calls.VoiceCall.CameraOff) pauses it, and [`SwitchCamera`](xref:Uniform.Matrix.Calls.VoiceCall.SwitchCamera) changes cameras. The peer's video is decoded by the platform's [`VideoDecoder`](xref:Uniform.Matrix.Calls.CallService.VideoDecoder), e.g. Android's hardware one, or by the C# decoder; a device that cannot keep up asks the sender for less, and for full quality again once it can.
4. **Ended.** `m.call.hangup` from either side, `m.call.reject`, a timeout, or a failed connection; [`EndReason`](xref:Uniform.Matrix.Calls.VoiceCall.EndReason) says which.

## Tests

Uniform.Chat.Server.Tests calls over MatrixRTC between two client engines of a homeserver with LiveKit on, when `LIVEKIT_SERVER` names LiveKit's server program. It checks the ring, the answer, encrypted voice both ways, and a hangup that ends the call on both sides.

Uniform.Matrix.Calls.Tests runs two legacy calls against each other in memory, with fake devices that record and play tones in real time: a call that each side must hear and then hang up, and a rejected call. Where Chromium is installed, it calls with a canvas as its camera, and the call must decode its pictures.
