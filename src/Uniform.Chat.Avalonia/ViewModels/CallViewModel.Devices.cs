namespace Uniform.Chat.Avalonia.ViewModels;

using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Calls;

// The camera, microphone and speaker menus of the call bar, and the choices kept for the next call.
public sealed partial class CallViewModel
{
    /// <summary>The speaker chosen, by <see cref="AudioDevice.Name"/>; null for the platform's default.</summary>
    [ObservableProperty]
    public partial string? SpeakerId { get; set; }

    /// <summary>The microphone chosen, by <see cref="AudioDevice.Name"/>; null for the platform's default.</summary>
    [ObservableProperty]
    public partial string? MicrophoneId { get; set; }

    /// <summary>The items of the camera menu: the cameras, the one in use checked, then turning the camera off or on.</summary>
    public ObservableCollection<DeviceMenuItem> CameraMenu { get; } = [];

    /// <summary>The items of the speaker menu: the default, then each output, the chosen one checked.</summary>
    public ObservableCollection<DeviceMenuItem> SpeakerMenu { get; } = [];

    /// <summary>The items of the microphone menu: the default, then each input, the chosen one checked.</summary>
    public ObservableCollection<DeviceMenuItem> MicrophoneMenu { get; } = [];

    /// <summary>Whether the platform lists audio devices to choose from.</summary>
    public bool CanChooseAudio => audio is not null;

    /// <summary>Start with the speaker and microphone kept from earlier calls, where they are still attached.</summary>
    private void StartWithPreferences()
    {
        // Each kept choice, if the platform lists it now
        if (audio is null || settings is null) return;
        IAudioDevices devices = audio(AudioChoice.Default);
        if (settings.CallSpeaker is string speaker && List(devices.ListSpeakers).Any(d => d.Name == speaker)) SpeakerId = speaker;
        // Done
        if (settings.CallMicrophone is string microphone && List(devices.ListMicrophones).Any(d => d.Name == microphone)) MicrophoneId = microphone;
    }

    /// <summary>Have a new camera start with the one kept from earlier calls, where the device has it.</summary>
    private IVideoCamera OpenPreferredCamera(IVideoCamera camera)
    {
        // The kept camera among this device's
        if (settings?.CallCamera is string id && camera.Cameras.Any(c => c.Id == id)) camera.SelectCamera(id);
        // Return camera
        return camera;
    }

    /// <summary>Fill the menus from the devices attached now, e.g. as a menu opens, since Bluetooth devices come and go.</summary>
    public void RefreshDevices()
    {
        // Cameras of the call, then audio devices of the platform
        Fill(CameraMenu, CameraItems());
        if (audio is null) return;
        IAudioDevices devices = audio(AudioChoice.Default);
        Fill(SpeakerMenu, AudioItems(List(devices.ListSpeakers), SpeakerId, HasEarpiece ? "Phone default" : "System default", SelectSpeakerAsync));
        // Done
        Fill(MicrophoneMenu, AudioItems(List(devices.ListMicrophones), MicrophoneId, HasEarpiece ? "Phone default" : "System default", SelectMicrophoneAsync));
    }

    /// <summary>Return the camera menu: each camera of the call, the one in use checked, then the switch to turn the camera off or on.</summary>
    private List<DeviceMenuItem> CameraItems()
    {
        // The call's cameras
        if (Current is not VoiceCall call || !call.SendsVideo) return [];
        string? current = call.CameraId;
        List<DeviceMenuItem> items = [.. call.Cameras.Select(c => new DeviceMenuItem(Checked(c.Name, c.Id == current), new RelayCommand(() => SelectCamera(c.Id))))];
        // Return them with the switch
        items.Add(new DeviceMenuItem(Checked(CameraOff ? "Turn camera on" : "Turn camera off", chosen: false), ToggleCameraCommand));
        return items;
    }

    /// <summary>Return an audio menu: the default, then each device, the chosen one checked.</summary>
    private static List<DeviceMenuItem> AudioItems(IReadOnlyList<AudioDevice> devices, string? chosen, string defaultName, Func<string?, Task> select)
    {
        // The default first; a choice no longer attached checks nothing
        List<DeviceMenuItem> items = [new(Checked(defaultName, chosen is null), new AsyncRelayCommand(() => select(null)))];
        items.AddRange(devices.Select(d => new DeviceMenuItem(Checked(d.Description, d.Name == chosen), new AsyncRelayCommand(() => select(d.Name)))));
        // Return items
        return items;
    }

    /// <summary>Send the camera of <paramref name="id"/>, and keep it for the next calls.</summary>
    private void SelectCamera(string id)
    {
        // The call switches; the choice is kept
        if (Current is not VoiceCall call) return;
        call.SelectCamera(id);
        if (settings is not null) settings.CallCamera = id;
        // Done
        Fill(CameraMenu, CameraItems());
    }

    /// <summary>Play through the speaker of <paramref name="name"/>, null for the default, reopening the devices of a running call, and keep it for the next calls.</summary>
    private async Task SelectSpeakerAsync(string? name)
    {
        // The choice, kept, then the devices
        SpeakerId = name;
        if (settings is not null) settings.CallSpeaker = name;
        HoldPlatform(IsRinging, IsActive);
        RefreshDevices();
        // Done
        if (Current is VoiceCall call) await RunAsync("Switching the speaker", () => call.RestartAudioAsync());
    }

    /// <summary>Record from the microphone of <paramref name="name"/>, null for the default, reopening the devices of a running call, and keep it for the next calls.</summary>
    private async Task SelectMicrophoneAsync(string? name)
    {
        // The choice, kept, then the devices
        MicrophoneId = name;
        if (settings is not null) settings.CallMicrophone = name;
        RefreshDevices();
        // Done
        if (Current is VoiceCall call) await RunAsync("Switching the microphone", () => call.RestartAudioAsync());
    }

    /// <summary>Return a menu label, with a check before the chosen one.</summary>
    private static string Checked(string name, bool chosen) => chosen ? $"✓ {name}" : $"    {name}";

    /// <summary>Return a device list, empty when the platform fails to list, e.g. without its sound system.</summary>
    private static IReadOnlyList<AudioDevice> List(Func<IReadOnlyList<AudioDevice>> list)
    {
        // As listed, or none
        try { return list(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return []; }
    }

    /// <summary>Replace the items of a menu.</summary>
    private static void Fill(ObservableCollection<DeviceMenuItem> menu, IEnumerable<DeviceMenuItem> items)
    {
        // All anew
        menu.Clear();
        foreach (DeviceMenuItem item in items) menu.Add(item);
    }
}

/// <summary>An item of a device menu of the call bar.</summary>
/// <param name="Label">What it shows, with a check when chosen.</param>
/// <param name="Command">What choosing it does.</param>
public sealed record DeviceMenuItem(string Label, ICommand Command);
