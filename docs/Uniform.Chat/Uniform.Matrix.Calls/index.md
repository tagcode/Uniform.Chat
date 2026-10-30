---
uid: Uniform.Matrix.Calls.Article
title: Uniform.Matrix.Calls
---
# Uniform.Matrix.Calls

Matrix [1:1 voice calls](https://spec.matrix.org/latest/client-server-api/#voice-over-ip), the `m.call.*` events of version 1, for the [client engine](xref:Uniform.Matrix.Client.Article). Media goes over [Uniform.Matrix.WebRtc](xref:Uniform.Matrix.WebRtc.Article), and voice through [Uniform.Matrix.Audio](xref:Uniform.Matrix.Audio.Article). Calls interoperate with Element and other clients of the legacy call protocol; group calls (MatrixRTC) are not supported yet.

## Service

[`CallService`](xref:Uniform.Matrix.Calls.CallService) is a singleton beside the client engine. It follows [`SyncProcessor.Applied`](xref:Uniform.Matrix.Client.Services.SyncProcessor.Applied), routes each call event to its call, and raises [`IncomingCall`](xref:Uniform.Matrix.Calls.CallService.IncomingCall) for a fresh invite to this user. Events go out through the [`OutboxSender`](xref:Uniform.Matrix.Client.Services.OutboxSender), which encrypts them in encrypted rooms, as Element does. TURN credentials come from the homeserver's `/voip/turnServer`, and are kept until shortly before they expire.

To add calls to an app, register the service, and set the devices when the app chooses them:

```csharp
builder.Services.AddSingleton<CallService>();
// …
calls.Devices = () => AudioDevices.ForThisPlatform(microphone, speaker);
calls.IncomingCall += call => ShowRinging(call);
VoiceCall call = await calls.PlaceCallAsync(roomId);
```

## A call

[`VoiceCall`](xref:Uniform.Matrix.Calls.VoiceCall) is one call:

1. **Ringing or inviting.** The caller sends `m.call.invite` with the offer, and the callee rings until it answers, rejects, or the invite's lifetime passes.
2. **Connecting.** The callee answers with `m.call.answer`; the caller picks the first answer with `m.call.select_answer`, so the user's other devices stop ringing. Candidates go both ways in `m.call.candidates` batches, ending with an empty candidate; those that arrive before the offer or answer wait for it.
3. **Connected.** [`VoiceMedia`](xref:Uniform.Matrix.Calls.VoiceMedia) sends the microphone as Opus, and plays what arrives through a jitter buffer, paced by the speaker. [`Muted`](xref:Uniform.Matrix.Calls.VoiceCall.Muted) stops sending.
4. **Ended.** `m.call.hangup` from either side, `m.call.reject`, a timeout, or a failed connection; [`EndReason`](xref:Uniform.Matrix.Calls.VoiceCall.EndReason) says which.

## Tests

Uniform.Matrix.Calls.Tests runs two calls against each other in memory, with fake devices that record and play tones in real time: a call that each side must hear and then hang up, and a rejected call.
