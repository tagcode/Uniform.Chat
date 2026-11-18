namespace Uniform.Chat.Android.Audio;

using global::Android.Content;
using global::Android.Media;
using Uniform.Matrix.Audio;

/// <summary>The phone's audio devices as a call chooses them, by names that stay the same across connections: <see cref="AudioChoice.Earpiece"/>, <see cref="AudioChoice.Loudspeaker"/>, <c>microphone</c>, and others by kind and product, e.g. <c>bluetooth:JBL Flip 5</c>.</summary>
/// <remarks>A Bluetooth device shows once, whether it is connected for calls (SCO or LE audio), for music (A2DP), or both; a call prefers the call connection, which also carries the device's microphone.</remarks>
internal static class AudioRoutes
{
    /// <summary>The name of the phone's own microphone.</summary>
    public const string BuiltInMicrophone = "microphone";

    /// <summary>Return the outputs a call can play to: earpiece, loudspeaker, then Bluetooth, wired and USB devices.</summary>
    public static IReadOnlyList<AudioDevice> Speakers(Context context)
    {
        // Built in first, then attached ones, each name once
        AudioDeviceInfo[] outputs = Devices(context, GetDevicesTargets.Outputs);
        List<AudioDevice> list = [];
        if (outputs.Any(d => d.Type == AudioDeviceType.BuiltinEarpiece)) list.Add(new AudioDevice(AudioChoice.Earpiece, "Earpiece"));
        if (outputs.Any(d => d.Type == AudioDeviceType.BuiltinSpeaker)) list.Add(new AudioDevice(AudioChoice.Loudspeaker, "Loudspeaker"));
        foreach (AudioDeviceInfo device in outputs)
        {
            if (NameOf(device) is not string name || list.Any(d => d.Name == name)) continue;
            list.Add(new AudioDevice(name, Describe(device)));
        }
        // Return list
        return list;
    }

    /// <summary>Return the inputs a call can record from: the phone's microphone, then those of Bluetooth, wired and USB headsets.</summary>
    public static IReadOnlyList<AudioDevice> Microphones(Context context)
    {
        // Built in first, then attached ones, each name once
        AudioDeviceInfo[] inputs = Devices(context, GetDevicesTargets.Inputs);
        List<AudioDevice> list = [];
        if (inputs.Any(d => d.Type == AudioDeviceType.BuiltinMic)) list.Add(new AudioDevice(BuiltInMicrophone, "Phone microphone"));
        foreach (AudioDeviceInfo device in inputs)
        {
            if (NameOf(device) is not string name || list.Any(d => d.Name == name)) continue;
            list.Add(new AudioDevice(name, Describe(device)));
        }
        // Return list
        return list;
    }

    /// <summary>Return the device a call can route to as its communication device, for output named <paramref name="name"/>; null when there is none, e.g. a speaker connected for music only.</summary>
    public static AudioDeviceInfo? CommunicationDevice(AudioManager audio, string name)
    {
        // Android 12 and later choose among the communication devices
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return null;
        AudioDeviceType? builtIn = name == AudioChoice.Earpiece ? AudioDeviceType.BuiltinEarpiece : name == AudioChoice.Loudspeaker ? AudioDeviceType.BuiltinSpeaker : null;
        // Return the match
        return audio.AvailableCommunicationDevices.FirstOrDefault(d => builtIn is AudioDeviceType type ? d.Type == type : NameOf(d) == name);
    }

    /// <summary>Return the device of <paramref name="name"/> among the outputs or inputs, a call connection before a music one; null when it is not connected.</summary>
    public static AudioDeviceInfo? Find(Context context, GetDevicesTargets direction, string name)
    {
        // The built-in names, then by kind and product
        AudioDeviceInfo[] devices = Devices(context, direction);
        AudioDeviceType? builtIn = name switch { AudioChoice.Earpiece => AudioDeviceType.BuiltinEarpiece, AudioChoice.Loudspeaker => AudioDeviceType.BuiltinSpeaker, BuiltInMicrophone => AudioDeviceType.BuiltinMic, _ => null };
        if (builtIn is AudioDeviceType type) return devices.FirstOrDefault(d => d.Type == type);
        // Return the match, calls first
        return devices.Where(d => NameOf(d) == name).OrderBy(d => d.Type is AudioDeviceType.BluetoothA2dp ? 1 : 0).FirstOrDefault();
    }

    /// <summary>Whether <paramref name="device"/> is connected for music only, so call audio reaches it only as media.</summary>
    public static bool IsMusicOnly(AudioDeviceInfo device) => device.Type == AudioDeviceType.BluetoothA2dp;

    /// <summary>Return the devices of a direction.</summary>
    private static AudioDeviceInfo[] Devices(Context context, GetDevicesTargets direction) => context.GetSystemService(Context.AudioService) is AudioManager audio ? audio.GetDevices(direction) ?? [] : [];

    /// <summary>Return the name of an attached device by kind and product, e.g. <c>bluetooth:JBL Flip 5</c>; null for built-in devices and kinds a call does not use.</summary>
    private static string? NameOf(AudioDeviceInfo device)
    {
        // Kinds a call can use
        string? kind = device.Type switch
        {
            AudioDeviceType.BluetoothSco or AudioDeviceType.BluetoothA2dp or AudioDeviceType.BleHeadset or AudioDeviceType.BleSpeaker or AudioDeviceType.HearingAid => "bluetooth",
            AudioDeviceType.WiredHeadset or AudioDeviceType.WiredHeadphones => "wired",
            AudioDeviceType.UsbHeadset or AudioDeviceType.UsbDevice => "usb",
            _ => null,
        };
        // Return kind and product
        return kind is null ? null : $"{kind}:{device.ProductName}";
    }

    /// <summary>Return what people see for an attached device, e.g. <c>JBL Flip 5 (Bluetooth)</c>.</summary>
    private static string Describe(AudioDeviceInfo device) => device.Type switch
    {
        AudioDeviceType.WiredHeadset => "Wired headset",
        AudioDeviceType.WiredHeadphones => "Wired headphones",
        AudioDeviceType.UsbHeadset or AudioDeviceType.UsbDevice => $"{device.ProductName} (USB)",
        _ => $"{device.ProductName} (Bluetooth)",
    };
}
