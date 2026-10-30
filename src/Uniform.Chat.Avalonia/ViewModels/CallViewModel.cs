namespace Uniform.Chat.Avalonia.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Calls;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.WebRtc.Ice;

/// <summary>The call bar: the voice call that rings or runs, with answering, declining, hanging up, muting and the loudspeaker; it tells about a call for a while after it ends.</summary>
public sealed partial class CallViewModel : ViewModelBase, IDisposable
{
    /// <summary>How long the bar stays after a call ends.</summary>
    private static readonly TimeSpan EndedShownFor = TimeSpan.FromSeconds(4);
    private readonly CallService calls;
    private readonly IUiDispatcher ui;
    private readonly GuiPlatform platform;
    private readonly Func<bool, IAudioDevices>? audio;
    private readonly Func<string, string> nameOf;
    private readonly Timer clock;
    private IDisposable? keptAlive;
    private IDisposable? ringing;
    private IDisposable? nearEar;

    /// <summary>Follow the calls of <paramref name="calls"/>.</summary>
    /// <param name="calls">The account's calls.</param>
    /// <param name="ui">Dispatcher of the user interface thread.</param>
    /// <param name="platform">The platform's audio, permissions, notifications, ringing and keeping calls alive.</param>
    /// <param name="nameOf">Returns a user's display name.</param>
    public CallViewModel(CallService calls, IUiDispatcher ui, GuiPlatform platform, Func<string, string> nameOf)
    {
        // Devices as chosen when voice starts, and the call events
        this.calls = calls;
        this.ui = ui;
        this.platform = platform;
        audio = platform.Audio;
        HasEarpiece = platform.HasEarpiece;
        this.nameOf = nameOf;
        calls.Devices = () => audio?.Invoke(HasEarpiece && Loudspeaker);
        calls.IncomingCall += OnIncoming;
        calls.CallChanged += OnChanged;
        clock = new Timer(_ => ui.Post(Refresh), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Whether the platform has audio, so calls can be made.</summary>
    public bool CanCall => audio is not null;

    /// <summary>Whether the loudspeaker is a choice.</summary>
    public bool HasEarpiece { get; }

    /// <summary>The call shown, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial VoiceCall? Current { get; set; }

    /// <summary>Whether the bar shows.</summary>
    public bool IsVisible => Current is not null;

    /// <summary>Who the call is with.</summary>
    [ObservableProperty]
    public partial string PeerName { get; set; } = "";

    /// <summary>What the call does, e.g. <c>Ringing…</c> or <c>01:23</c>.</summary>
    [ObservableProperty]
    public partial string StateText { get; set; } = "";

    /// <summary>Whether an incoming call rings, to answer or decline.</summary>
    [ObservableProperty]
    public partial bool IsRinging { get; set; }

    /// <summary>Whether the call goes on, to hang up.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Whether the microphone is muted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteText))]
    public partial bool IsMuted { get; set; }

    /// <summary>Text of the mute button.</summary>
    public string MuteText => IsMuted ? "Unmute" : "Mute";

    /// <summary>Whether to play through the loudspeaker rather than the earpiece; only where <see cref="HasEarpiece"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeakerText))]
    public partial bool Loudspeaker { get; set; }

    /// <summary>Text of the speaker button.</summary>
    public string SpeakerText => Loudspeaker ? "Earpiece" : "Speaker";

    /// <summary>A failure to show, e.g. a missing microphone permission; empty for none.</summary>
    [ObservableProperty]
    public partial string Error { get; set; } = "";

    /// <summary>Call the other member of a direct room, asking for the microphone first where needed.</summary>
    public async Task PlaceCallAsync(string roomId)
    {
        // Permission, then the call
        Error = "";
        if (!await HasMicrophoneAsync()) return;
        try
        {
            Show(await calls.PlaceCallAsync(roomId));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // On a room that cannot be called, or the server
            Error = $"Calling failed: {ex.Message}";
        }
    }

    /// <summary>Answer the ringing call.</summary>
    [RelayCommand]
    private async Task AnswerAsync()
    {
        // Permission, then the answer
        if (Current is not VoiceCall call) return;
        if (!await HasMicrophoneAsync()) return;
        await RunAsync("Answering", () => call.AnswerAsync());
    }

    /// <summary>Decline the ringing call.</summary>
    [RelayCommand]
    private Task RejectAsync() => Current is VoiceCall call ? RunAsync("Declining", () => call.RejectAsync()) : Task.CompletedTask;

    /// <summary>Hang up, or cancel a call that rings.</summary>
    [RelayCommand]
    private Task HangupAsync() => Current is VoiceCall call ? RunAsync("Hanging up", () => call.HangupAsync()) : Task.CompletedTask;

    /// <summary>Mute or unmute the microphone.</summary>
    [RelayCommand]
    private void ToggleMute()
    {
        // The call keeps it
        if (Current is not VoiceCall call) return;
        call.Muted = !call.Muted;
    }

    /// <summary>Switch between the earpiece and the loudspeaker, reopening the devices of a running call.</summary>
    [RelayCommand]
    private async Task ToggleSpeakerAsync()
    {
        // The choice, then the devices
        Loudspeaker = !Loudspeaker;
        HoldPlatform(IsRinging, IsActive);
        if (Current is VoiceCall call) await RunAsync("Switching the speaker", () => call.RestartAudioAsync());
    }

    /// <summary>Stop following the calls.</summary>
    public void Dispose()
    {
        // Events and the clock
        calls.IncomingCall -= OnIncoming;
        calls.CallChanged -= OnChanged;
        clock.Dispose();
        HoldPlatform(false, false);
    }

    /// <summary>Hold what the platform does for the call's state: keep it alive while active, ring while ringing, and turn the screen off near the ear while active on the earpiece.</summary>
    private void HoldPlatform(bool ringingNow, bool active)
    {
        // Each started once and stopped once
        Hold(ref keptAlive, active, platform.KeepCallAlive is { } keep ? () => keep(PeerName) : null);
        Hold(ref ringing, ringingNow, platform.Ring is { } ring ? () => ring(PeerName) : null);
        Hold(ref nearEar, active && !(HasEarpiece && Loudspeaker), platform.ScreenOffNearEar);
    }

    /// <summary>Start a platform action when wanted and not held, or stop it when held and no longer wanted.</summary>
    private static void Hold(ref IDisposable? held, bool wanted, Func<IDisposable>? start)
    {
        // Start
        if (wanted && held is null && start is not null) held = start();
        if (wanted || held is null) return;
        // Stop
        held.Dispose();
        held = null;
    }

    /// <summary>Ring: show the call, and notify for when the app is in the background.</summary>
    private void OnIncoming(VoiceCall call)
    {
        // Bar, then the notification
        ui.Post(() => Show(call));
        if (platform.Ring is null && platform.Notifier is INotifier notifier) _ = notifier.ShowAsync("Incoming call", $"{nameOf(call.PeerUserId)} is calling");
    }

    /// <summary>Follow the shown call's changes; a new call replaces an ended one.</summary>
    private void OnChanged(VoiceCall call) => ui.Post(() =>
    {
        // The shown call, or one that replaces an ended one
        if (Current is not null && Current != call && Current.State != CallState.Ended) return;
        Show(call);
    });

    /// <summary>Show a call, and hide it a while after it ends.</summary>
    private void Show(VoiceCall call)
    {
        // Shown
        Current = call;
        PeerName = nameOf(call.PeerUserId);
        Refresh();
        if (call.State != CallState.Ended) return;
        // Hidden later, unless another call came
        _ = Task.Delay(EndedShownFor).ContinueWith(_ => ui.Post(() => { if (Current == call) Current = null; }), TaskScheduler.Default);
    }

    /// <summary>Update the texts and buttons from the call, every second for the duration.</summary>
    private void Refresh()
    {
        // Nothing shown
        if (Current is not VoiceCall call) return;
        IsRinging = call.State == CallState.Ringing;
        IsActive = call.State is not (CallState.Ringing or CallState.Ended);
        IsMuted = call.Muted;
        HoldPlatform(IsRinging, IsActive);
        bool relayed = call.Path.Local?.Type == IceCandidateType.Relayed || call.Path.Remote?.Type == IceCandidateType.Relayed;
        // Done
        StateText = call.State switch
        {
            CallState.Ringing => "Incoming voice call",
            CallState.Inviting => "Calling…",
            CallState.Connecting => "Connecting…",
            CallState.Connected => $"{Duration(call)}{(relayed ? " · relayed" : "")}{(call.Muted ? " · muted" : "")}",
            CallState.Reconnecting => "Reconnecting…",
            _ => $"Call ended: {EndText(call)}",
        };
    }

    /// <summary>Return the time since voice started, as <c>m:ss</c>.</summary>
    private static string Duration(VoiceCall call) => call.ConnectedAt is DateTimeOffset start ? (DateTimeOffset.UtcNow - start).ToString(@"m\:ss") : "";

    /// <summary>Return why a call ended, for people.</summary>
    private static string EndText(VoiceCall call) => call.EndReason switch
    {
        "user_hangup" => call.EndedByPeer ? "they hung up" : "you hung up",
        "rejected" => call.EndedByPeer ? "declined" : "you declined",
        "invite_timeout" => call.IsIncoming ? "missed" : "no answer",
        "ice_failed" => "no connection",
        "answered_elsewhere" => "answered on another device",
        "rejected_elsewhere" => "declined on another device",
        "replaced" => "they called again",
        string reason => reason,
        null => "ended",
    };

    /// <summary>Return whether the microphone may be used, asking where needed, and say so when not.</summary>
    private async Task<bool> HasMicrophoneAsync()
    {
        // Granted, or no permission needed
        if (platform.RequestMicrophoneAsync is not { } request || await request()) return true;
        Error = "Calls need the microphone permission.";
        // On refusal
        return false;
    }

    /// <summary>Run a call action, showing what failed.</summary>
    private async Task RunAsync(string what, Func<Task> action)
    {
        // Errors to the bar
        Error = "";
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = $"{what} failed: {ex.Message}";
        }
    }
}
