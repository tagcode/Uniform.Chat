---
uid: Uniform.Matrix.Audio.Article
title: Uniform.Matrix.Audio
---
# Uniform.Matrix.Audio

Voice audio for calls, the first part of calling: microphone and speaker through [NAudio](https://github.com/naudio/NAudio), and the [Opus](https://opus-codec.org/) codec through [Concentus](https://github.com/lostromb/concentus), a pure C# port of libopus. Nothing here knows about Matrix or the network; [Uniform.Matrix.Calls](xref:Uniform.Matrix.Calls.Article) builds calls on it.

## Voice frames

Calls carry voice as 20 ms frames of 48 kHz mono 16-bit samples ([`VoiceFormat`](xref:Uniform.Matrix.Audio.VoiceFormat)), each one an Opus packet:

- [`VoiceEncoder`](xref:Uniform.Matrix.Audio.VoiceEncoder) encodes a frame, tuned for speech: the VoIP application, 32 kbit/s by default, and in-band forward error correction.
- [`VoiceDecoder`](xref:Uniform.Matrix.Audio.VoiceDecoder) decodes a packet back into a frame; given no packet, it conceals the loss from the frames before.

Concentus would switch to a native libopus where it finds one; both classes turn that off, so every platform runs the same code.

## Devices

[`IAudioDevices`](xref:Uniform.Matrix.Audio.IAudioDevices) opens a platform's microphone as an NAudio [`IWaveIn`](https://github.com/naudio/NAudio) and its speaker as an `IWavePlayer`. [`AudioDevices.ForThisPlatform`](xref:Uniform.Matrix.Audio.AudioDevices.ForThisPlatform*) returns:

| Platform | Class | Through |
|---|---|---|
| Linux | [`AlsaAudioDevices`](xref:Uniform.Matrix.Audio.AlsaAudioDevices) | NAudio.Alsa's `AlsaIn` and `AlsaOut` on ALSA's default devices, which desktops route through PipeWire or PulseAudio |
| Windows | [`WasapiAudioDevices`](xref:Uniform.Matrix.Audio.WasapiAudioDevices) | NAudio.Wasapi, in shared mode |
| Android | `AndroidAudioDevices` in [Uniform.Chat.Android](xref:Uniform.Chat.Android.Article) | Android's `AudioRecord` from the voice communication source, with the phone's echo cancellation and noise suppression, and `AudioTrack` as call audio on the earpiece or the loudspeaker |

Devices record and play in their own formats. [`MicrophoneFrames`](xref:Uniform.Matrix.Audio.MicrophoneFrames) turns any `IWaveIn` into voice frames, mixing channels to mono and resampling to 48 kHz; [`SpeakerFrames`](xref:Uniform.Matrix.Audio.SpeakerFrames) plays frames through any `IWavePlayer`, with a short buffer against uneven arrival and silence in the gaps.

## Hearing yourself

[`VoiceLoopback`](xref:Uniform.Matrix.Audio.VoiceLoopback) plays the microphone back through the speaker, through Opus and a delay, to test the whole path without a call. The terminal client's `/audiotest` runs it:

```csharp
IAudioDevices devices = AudioDevices.ForThisPlatform() ?? throw new PlatformNotSupportedException();
using VoiceLoopback loopback = new(devices, TimeSpan.FromSeconds(1));
loopback.Start();
```

## Choosing devices

[`AudioDevices.ForThisPlatform`](xref:Uniform.Matrix.Audio.AudioDevices.ForThisPlatform*) takes a microphone and a speaker by name: on Linux an ALSA device name such as `pulse` or `plughw:CARD=MKII,DEV=0`, on Windows part of the device's friendly name. [`ListMicrophones`](xref:Uniform.Matrix.Audio.IAudioDevices.ListMicrophones*) and [`ListSpeakers`](xref:Uniform.Matrix.Audio.IAudioDevices.ListSpeakers*) return the choices. The terminal client's `Audio:Microphone` and `Audio:Speaker` settings pass them on.

On Linux, ALSA's `default` and `pulse` devices go through PipeWire's PulseAudio layer, which follows the `PULSE_SOURCE` and `PULSE_SINK` environment variables, so one process can be pointed at other PipeWire devices.

## Tests

`Uniform.Matrix.Audio.Tests` runs mostly without devices: a tone through Opus keeps its pitch and level, a lost packet is concealed, a 44.1 kHz stereo microphone becomes 48 kHz mono frames, and queued frames reach the player unchanged.

`PipeWireTests` goes through real ALSA and PipeWire with virtual devices it creates for itself with `pactl load-module module-null-sink`: a 500 Hz and a 1000 Hz tone played into a virtual microphone must come out of a virtual speaker, through the loopback, at their pitch and level. The variables above point the test's process at those devices. It removes the devices afterwards, and skips itself where no sound server runs, as on build servers.

To see what such a test hears, record a virtual speaker with a short latency and interrupt the recorder rather than kill it; otherwise its last seconds stay unwritten.

```bash
parecord --raw --latency-msec=20 --device=test_speaker.monitor --channels=1 --rate=48000 --format=s16le out.raw
```
