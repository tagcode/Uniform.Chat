---
uid: Uniform.Matrix.Audio.Article
title: Uniform.Matrix.Audio
---
# Uniform.Matrix.Audio

Voice audio for calls, the first part of calling: microphone and speaker through [NAudio](https://github.com/naudio/NAudio), and the [Opus](https://opus-codec.org/) codec through [Concentus](https://github.com/lostromb/concentus), a pure C# port of libopus. It also decodes and plays audio files, for audio messages. Nothing here knows about Matrix or the network; [Uniform.Matrix.Calls](xref:Uniform.Matrix.Calls.Article) builds calls on it.

## Voice frames

Calls carry voice as 20 ms frames of 48 kHz mono 16-bit samples ([`VoiceFormat`](xref:Uniform.Matrix.Audio.VoiceFormat)), each one an Opus packet:

- [`VoiceEncoder`](xref:Uniform.Matrix.Audio.VoiceEncoder) encodes a frame, tuned for speech: the VoIP application, 64 kbit/s by default for full-band speech, and in-band forward error correction.
- [`VoiceDecoder`](xref:Uniform.Matrix.Audio.VoiceDecoder) decodes a packet back into a frame; given no packet, it conceals the loss from the frames before.

Concentus would switch to a native libopus where it finds one; both classes turn that off, so every platform runs the same code.

## Devices

[`IAudioDevices`](xref:Uniform.Matrix.Audio.IAudioDevices) opens a platform's microphone as an NAudio [`IWaveIn`](https://github.com/naudio/NAudio) and its speaker as an `IWavePlayer`. [`AudioDevices.ForThisPlatform`](xref:Uniform.Matrix.Audio.AudioDevices.ForThisPlatform*) returns:

| Platform | Class | Through |
|---|---|---|
| Linux | [`AlsaAudioDevices`](xref:Uniform.Matrix.Audio.AlsaAudioDevices) | NAudio.Alsa's `AlsaIn` and `AlsaOut` on ALSA's default devices, which desktops route through PipeWire or PulseAudio |
| Windows | [`WasapiAudioDevices`](xref:Uniform.Matrix.Audio.WasapiAudioDevices) | NAudio.Wasapi, in shared mode |
| Android | `AndroidAudioDevices` in [Uniform.Chat.Android](xref:Uniform.Chat.Android.Article) | Android's `AudioRecord` from the voice communication source, with the phone's echo cancellation and noise suppression, and `AudioTrack` as call audio on the earpiece, the loudspeaker, or a Bluetooth, wired or USB device; a Bluetooth speaker connected for music only gets it as media |

Devices record and play in their own formats. [`MicrophoneFrames`](xref:Uniform.Matrix.Audio.MicrophoneFrames) turns any `IWaveIn` into voice frames, mixing channels to mono and resampling to 48 kHz; [`SpeakerFrames`](xref:Uniform.Matrix.Audio.SpeakerFrames) plays frames through any `IWavePlayer`, with a short buffer against uneven arrival and silence in the gaps. ALSA's default devices convert formats themselves; WASAPI's shared mode doesn't change sample rates, so on Windows the speaker resamples voice to the device's mix format, such as 44.1 kHz stereo, before handing it over.

## Hearing yourself

[`VoiceLoopback`](xref:Uniform.Matrix.Audio.VoiceLoopback) plays the microphone back through the speaker, through Opus and a delay, to test the whole path without a call. The terminal client's `/audiotest` runs it:

```csharp
IAudioDevices devices = AudioDevices.ForThisPlatform() ?? throw new PlatformNotSupportedException();
using VoiceLoopback loopback = new(devices, TimeSpan.FromSeconds(1));
loopback.Start();
```

## Choosing devices

[`AudioDevices.ForThisPlatform`](xref:Uniform.Matrix.Audio.AudioDevices.ForThisPlatform*) takes a microphone and a speaker by name: on Linux an ALSA device name such as `pulse` or `plughw:CARD=MKII,DEV=0`, on Windows part of the device's friendly name. [`ListMicrophones`](xref:Uniform.Matrix.Audio.IAudioDevices.ListMicrophones*) and [`ListSpeakers`](xref:Uniform.Matrix.Audio.IAudioDevices.ListSpeakers*) return the choices. The terminal client's `Audio:Microphone` and `Audio:Speaker` settings pass them on.

An [`AudioChoice`](xref:Uniform.Matrix.Audio.AudioChoice) names the microphone and speaker a call uses, by the [`Name`](xref:Uniform.Matrix.Audio.AudioDevice.Name) of each from [`ListMicrophones`](xref:Uniform.Matrix.Audio.IAudioDevices.ListMicrophones) and [`ListSpeakers`](xref:Uniform.Matrix.Audio.IAudioDevices.ListSpeakers); null is the platform's default. The apps open their devices with it, so the call bar's menus can choose them and remember the choice. On phones, [`AudioChoice.Earpiece`](xref:Uniform.Matrix.Audio.AudioChoice.Earpiece) and [`AudioChoice.Loudspeaker`](xref:Uniform.Matrix.Audio.AudioChoice.Loudspeaker) are speakers too.

On Linux, ALSA's `default` and `pulse` devices go through PipeWire's PulseAudio layer, which follows the `PULSE_SOURCE` and `PULSE_SINK` environment variables, so one process can be pointed at other PipeWire devices.

## Audio files

[`AudioFile.Decode`](xref:Uniform.Matrix.Audio.Decoding.AudioFile.Decode*) decodes a whole file into an [`AudioClip`](xref:Uniform.Matrix.Audio.Decoding.AudioClip): 16-bit samples at the file's own rate, mono or stereo, with more channels mixed down to two. [`AudioFile.Detect`](xref:Uniform.Matrix.Audio.Decoding.AudioFile.Detect*) recognizes the format by the file's first bytes, and [`AudioFile.IsPlayable`](xref:Uniform.Matrix.Audio.Decoding.AudioFile.IsPlayable*) tells from a MIME type or file name, before a download, whether a file is likely one to play.

| Format | Decoder |
|---|---|
| WAV, integer PCM of 8 to 32 bits or float | NAudio's `WaveFileReader` |
| MP3, MPEG-1, 2 and 2.5 layer III | `Mp3File`, in C#, copied from the same author's Uniform.Audio.Sampling.Encoding; trims the encoder delay and padding a LAME header tells |
| FLAC, up to 24 bits, and 32 without stereo decorrelation | `FlacFile`, in C#, per [RFC 9639](https://www.rfc-editor.org/rfc/rfc9639) |
| Ogg Opus, as Element's voice messages are | `OggPackets` reads the Ogg pages, and Concentus decodes at 48 kHz; the pre-skip and the last granule position trim the start and end, and the output gain is applied |
| Ogg Vorbis | [NVorbis](https://github.com/NVorbis/NVorbis) |

AAC, as in `.m4a` files, is not supported: such messages keep *Open* and *Save*. A clip is at most [`AudioFile.DefaultMaxSamples`](xref:Uniform.Matrix.Audio.Decoding.AudioFile.DefaultMaxSamples), 128 MB of samples, about 12 minutes of stereo at 44.1 kHz; a longer file throws `NotSupportedException`, and a broken one `InvalidDataException`.

[`AudioClipPlayer`](xref:Uniform.Matrix.Audio.AudioClipPlayer) plays a clip: play, pause, resume, seek and stop, with the [`Position`](xref:Uniform.Matrix.Audio.AudioClipPlayer.Position) played and [`Ended`](xref:Uniform.Matrix.Audio.AudioClipPlayer.Ended) at the end. It converts the clip to 48 kHz stereo, [`OutputFormat`](xref:Uniform.Matrix.Audio.AudioClipPlayer.OutputFormat), with NAudio's resampler, and opens the speaker anew for each start, on the thread pool. It plays on [`IAudioDevices.OpenMediaSpeaker`](xref:Uniform.Matrix.Audio.IAudioDevices.OpenMediaSpeaker), which is the call speaker on desktops; Android plays media on its media stream instead of the earpiece.

```csharp
AudioClip clip = await AudioFile.DecodeFileAsync("voice.ogg");
IAudioDevices devices = AudioDevices.ForThisPlatform() ?? throw new PlatformNotSupportedException();
await using AudioClipPlayer player = new(devices.OpenMediaSpeaker);
await player.PlayAsync(clip);
```

## Tests

`Uniform.Matrix.Audio.Tests` runs mostly without devices: a tone through Opus keeps its pitch and level, a lost packet is concealed, a 44.1 kHz stereo microphone becomes 48 kHz mono frames, and queued frames reach the player unchanged.

`AudioFileTests` decodes files in `Resources/`: MP3s of each MPEG version against mpg123's output, FLACs made by ffmpeg at different compression levels against their lossless sources, sample for sample, and Ogg Opus and Vorbis against libopus and libvorbis, through ffmpeg, by signal-to-noise ratio and exact length. `AudioClipPlayerTests` plays, pauses, seeks and ends a clip on a speaker that pulls in real time.

`PipeWireTests` goes through real ALSA and PipeWire with virtual devices it creates for itself with `pactl load-module module-null-sink`: a 500 Hz and a 1000 Hz tone played into a virtual microphone must come out of a virtual speaker, through the loopback, at their pitch and level. The variables above point the test's process at those devices. It removes the devices afterwards, and skips itself where no sound server runs, as on build servers.

To see what such a test hears, record a virtual speaker with a short latency and interrupt the recorder rather than kill it; otherwise its last seconds stay unwritten.

```bash
parecord --raw --latency-msec=20 --device=test_speaker.monitor --channels=1 --rate=48000 --format=s16le out.raw
```
