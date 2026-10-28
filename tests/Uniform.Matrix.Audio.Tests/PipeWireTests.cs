namespace Uniform.Matrix.Audio.Tests;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>The voice path through real ALSA and PipeWire, with virtual devices this test creates and removes: a tone played into a virtual microphone must come out of a virtual speaker. Skipped where PipeWire or PulseAudio is not running.</summary>
public class PipeWireTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Two tones go through the loopback at their pitch and level, and the devices are listed.</summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task LoopThroughVirtualDevices()
    {
        // Assert a sound server, on Linux
        if (!OperatingSystem.IsLinux() || await RunAsync("pactl", "info") is null) Assert.Skip("No PipeWire or PulseAudio here.");
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string microphone = $"uniform_test_mic_{suffix}", speaker = $"uniform_test_speaker_{suffix}";
        List<string> modules = [];
        string directory = Directory.CreateTempSubdirectory("uniform-audio-test-").FullName;
        try
        {
            // Arrange: virtual devices, once the sound server lists them, as it otherwise falls back to the real ones; a signal of 500 Hz then 1000 Hz, and a recording of the speaker
            foreach (string name in new[] { microphone, speaker }) modules.Add((await RunAsync("pactl", $"load-module module-null-sink sink_name={name}"))!.Trim());
            for (int wait = 0; wait < 50 && !((await RunAsync("pactl", "list short sources")) is string sources && sources.Contains($"{microphone}.monitor") && sources.Contains($"{speaker}.monitor")); wait++) await Task.Delay(100, Ct);
            string signal = Path.Combine(directory, "signal.wav"), recorded = Path.Combine(directory, "recorded.raw");
            WriteWave(signal, [.. Tone(500, 1), .. Tone(1000, 1)]);
            using Process recorder = Process.Start(new ProcessStartInfo("parecord", $"--raw --latency-msec=20 --process-time-msec=20 --device={speaker}.monitor --channels=1 --rate=48000 --format=s16le {recorded}"))!;

            // Act: the loopback on ALSA's pulse device, pointed at the virtual devices through the native environment, which the PulseAudio plugin reads
            SetNativeVariable("PULSE_SOURCE", $"{microphone}.monitor");
            SetNativeVariable("PULSE_SINK", speaker);
            AlsaAudioDevices devices = new("pulse", "pulse");
            IReadOnlyList<AudioDevice> microphones = devices.ListMicrophones();
            using (VoiceLoopback loopback = new(devices, TimeSpan.FromMilliseconds(500)))
            {
                loopback.Start();
                for (int wait = 0; loopback.Frames < 10 && wait < 100; wait++) await Task.Delay(50, Ct);
                await RunAsync("paplay", $"--device={microphone} {signal}");
                await Task.Delay(1500, Ct);
            }
            await RunAsync("kill", $"-INT {recorder.Id}");
            await recorder.WaitForExitAsync(Ct);

            // Assert: both tones, at about the level sent; the recorder, interrupted rather than killed, has written all it buffered
            short[] samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(await File.ReadAllBytesAsync(recorded, Ct)).ToArray();
            List<(double Frequency, double Rms)> windows = [.. Enumerable.Range(0, samples.Length / 4800).Select(w => Analyze(samples.AsSpan(w * 4800, 4800)))];
            Assert.Contains(microphones, m => m.Name == "pulse");
            string heard = $"{samples.Length / 48000.0:F1} s recorded: " + string.Join(" ", windows.Select(w => w.Rms > 1000 ? $"{w.Frequency:F0}" : "-"));
            Assert.True(windows.Count(w => w.Rms > 1000 && Math.Abs(w.Frequency - 500) < 30) is >= 7 and <= 11, heard);
            Assert.True(windows.Count(w => w.Rms > 1000 && Math.Abs(w.Frequency - 1000) < 30) is >= 7 and <= 11, heard);
            Assert.InRange(windows.Max(w => w.Rms) / (0.4 * 32767 / Math.Sqrt(2)), 0.7, 1.3);
        }
        finally
        {
            // Devices and files away
            SetNativeVariable("PULSE_SOURCE", null);
            SetNativeVariable("PULSE_SINK", null);
            foreach (string module in modules) await RunAsync("pactl", $"unload-module {module}");
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Set or remove a variable of the process's native environment, which .NET's own setter leaves alone on Linux.</summary>
    private static void SetNativeVariable(string name, string? value)
    {
        // libc keeps the native environment
        if (value is null) _ = unsetenv(name);
        else _ = setenv(name, value, 1);
    }

    /// <summary>libc's setenv.</summary>
    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern int setenv(string name, string value, int overwrite);

    /// <summary>libc's unsetenv.</summary>
    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern int unsetenv(string name);

    /// <summary>Return a second-long tone, or as long as asked, at 48 kHz.</summary>
    private static short[] Tone(double frequency, double seconds) =>
        [.. Enumerable.Range(0, (int)(48000 * seconds)).Select(i => (short)(0.4 * 32767 * Math.Sin(2 * Math.PI * frequency * i / 48000)))];

    /// <summary>Return the frequency, by rising zero crossings, and level of a stretch of samples.</summary>
    private static (double Frequency, double Rms) Analyze(ReadOnlySpan<short> samples)
    {
        // Crossings and energy
        int crossings = 0;
        double energy = samples[0] * (double)samples[0];
        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0) crossings++;
            energy += samples[i] * (double)samples[i];
        }
        // Return both
        return (crossings * 48000.0 / samples.Length, Math.Sqrt(energy / samples.Length));
    }

    /// <summary>Write 48 kHz mono 16-bit samples as a WAV file.</summary>
    private static void WriteWave(string path, short[] samples)
    {
        // Header and data
        using NAudio.Wave.WaveFileWriter writer = new(path, VoiceFormat.WaveFormat);
        writer.WriteSamples(samples, 0, samples.Length);
    }

    /// <summary>Run a command and return its output, or null when it fails or does not exist.</summary>
    private static async Task<string?> RunAsync(string command, string arguments)
    {
        // Run to the end
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(command, arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            string output = await process.StandardOutput.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);
            // Return output when it worked
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // On error the command is missing
            return null;
        }
    }
}
