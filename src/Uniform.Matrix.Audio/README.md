# Uniform.Matrix.Audio

Voice audio for Matrix calls: 20 ms voice frames from a microphone and to a speaker over [NAudio](https://github.com/naudio/NAudio), with ALSA on Linux and WASAPI on Windows, and Opus encoding and decoding with [Concentus](https://github.com/lostromb/concentus) in pure C#.

```bash
dotnet add package Uniform.Matrix.Audio
```

To hear yourself through Opus a second later, testing the microphone and speaker:

```csharp
using Uniform.Matrix.Audio;

IAudioDevices devices = AudioDevices.ForThisPlatform() ?? throw new PlatformNotSupportedException();
using VoiceLoopback loopback = new(devices, TimeSpan.FromSeconds(1));
loopback.Start();
Console.ReadLine();
```

To encode and decode frames yourself, with [`MicrophoneFrames`](https://github.com/tagcode/Uniform.Matrix) delivering 960-sample frames from any NAudio `IWaveIn`:

```csharp
VoiceEncoder encoder = new();
VoiceDecoder decoder = new();
byte[] packet = encoder.Encode(frame);
short[] played = decoder.Decode(packet);
```
