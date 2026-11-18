namespace Uniform.Chat.Avalonia.Tests;

using global::Avalonia.Headless.XUnit;
using NAudio.Wave;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Matrix.Audio;
using static Uniform.Chat.Avalonia.Tests.ChatFlowTests;

/// <summary>The speaker and microphone menus of the call bar, and the choices kept for the next call.</summary>
public class CallDeviceTests
{
    /// <summary>A kept speaker that is attached is chosen at the next start, a kept microphone that is gone is not; choosing in a menu checks the item and keeps the choice.</summary>
    [AvaloniaFact]
    public async Task KeepChosenDevices()
    {
        // Arrange: kept choices, and devices with the speaker but without the microphone
        await using LocalServer server = await LocalServer.StartAsync();
        Matrix.Protocol.Client.MatrixClient alice = await server.RegisterAsync("alice");
        List<AudioChoice> opened = [];
        GuiPlatform platform = server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName) with
        {
            ApplicationName = $"Uniform.Chat.Avalonia.Tests.{Guid.NewGuid():N}",
            Audio = choice => { opened.Add(choice); return new ListedDevices(); },
        };
        MainViewModel model = new(platform, new AvaloniaUiDispatcher());
        model.Settings.CallSpeaker = "bluetooth:Speaker";
        model.Settings.CallMicrophone = "bluetooth:Gone";

        try
        {
            // Act: start, and open the menus
            ChatViewModel chat = await SignInAsync(model, alice.UserId!);
            CallViewModel call = chat.Call!;
            call.RefreshDevices();

            // Assert: the attached speaker chosen and checked, the missing microphone left to the default
            Assert.Equal("bluetooth:Speaker", call.SpeakerId);
            Assert.Null(call.MicrophoneId);
            Assert.Equal(["    System default", "✓ Speaker (Bluetooth)"], call.SpeakerMenu.Select(i => i.Label));
            Assert.Equal(["✓ System default", "    Built-in microphone"], call.MicrophoneMenu.Select(i => i.Label));

            // Act: choose the built-in microphone and the default speaker
            call.MicrophoneMenu[1].Command.Execute(null);
            call.SpeakerMenu[0].Command.Execute(null);

            // Done: chosen, kept, and given to the devices of the next call
            await WaitUntilAsync(() => call.MicrophoneId == "builtin" && call.SpeakerId is null, "the choices");
            Assert.Equal("builtin", model.Settings.CallMicrophone);
            Assert.Null(model.Settings.CallSpeaker);
            Assert.Equal("✓ Built-in microphone", call.MicrophoneMenu[1].Label);
        }
        finally
        {
            await model.StopAsync();
        }
    }

    /// <summary>Devices that list a built-in microphone and a Bluetooth speaker, and open nothing.</summary>
    private sealed class ListedDevices : IAudioDevices
    {
        /// <summary>Not used.</summary>
        public IWaveIn OpenMicrophone() => throw new NotSupportedException();
        /// <summary>Not used.</summary>
        public IWavePlayer OpenSpeaker() => throw new NotSupportedException();
        /// <summary>The built-in microphone.</summary>
        public IReadOnlyList<AudioDevice> ListMicrophones() => [new("builtin", "Built-in microphone")];
        /// <summary>A Bluetooth speaker.</summary>
        public IReadOnlyList<AudioDevice> ListSpeakers() => [new("bluetooth:Speaker", "Speaker (Bluetooth)")];
    }
}
