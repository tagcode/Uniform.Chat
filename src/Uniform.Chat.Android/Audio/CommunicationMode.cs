namespace Uniform.Chat.Android.Audio;

using global::Android.Content;
using global::Android.Media;

/// <summary>Android's communication mode for the length of a call, shared by its microphone and speaker: in it the phone routes voice to the earpiece and runs its own echo cancellation between them.</summary>
/// <remarks>The microphone and the speaker each enter it when they start and leave when they stop; the mode goes back to normal when both have left.</remarks>
internal static class CommunicationMode
{
    private static readonly Lock Gate = new();
    private static int users;

    /// <summary>Enter communication mode, before opening the microphone or speaker, so that the phone's echo cancellation applies to them.</summary>
    public static void Enter(Context context)
    {
        // The first user switches
        lock (Gate)
        {
            if (users++ > 0 || context.GetSystemService(Context.AudioService) is not AudioManager audio) return;
            audio.Mode = Mode.InCommunication;
        }
    }

    /// <summary>Leave communication mode; the last user switches back to normal and to the default device.</summary>
    public static void Exit(Context context)
    {
        // The last user switches back
        lock (Gate)
        {
            if (users == 0 || --users > 0 || context.GetSystemService(Context.AudioService) is not AudioManager audio) return;
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) audio.ClearCommunicationDevice();
            audio.Mode = Mode.Normal;
        }
    }
}
