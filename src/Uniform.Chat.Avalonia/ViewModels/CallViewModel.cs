namespace Uniform.Chat.Avalonia.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Calls;
using Uniform.Matrix.Video;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.WebRtc.Ice;

/// <summary>The call bar: the voice call that rings or runs, with answering, declining, hanging up, muting and the loudspeaker, and the caller's video when they send it; it tells about a call for a while after it ends.</summary>
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
    private readonly Func<bool> mayRing;
    private IDisposable? nearEar;
    private int pictured;

    /// <summary>Follow the calls of <paramref name="calls"/>.</summary>
    /// <param name="calls">The account's calls.</param>
    /// <param name="ui">Dispatcher of the user interface thread.</param>
    /// <param name="platform">The platform's audio, permissions, notifications, ringing and keeping calls alive.</param>
    /// <param name="nameOf">Returns a user's display name.</param>
    /// <param name="mayRing">Returns whether incoming calls ring and notify, as the settings choose; null always rings.</param>
    public CallViewModel(CallService calls, IUiDispatcher ui, GuiPlatform platform, Func<string, string> nameOf, Func<bool>? mayRing = null)
    {
        // Devices as chosen when voice starts, and the call events
        this.calls = calls;
        this.ui = ui;
        this.platform = platform;
        audio = platform.Audio;
        HasEarpiece = platform.HasEarpiece;
        this.nameOf = nameOf;
        this.mayRing = mayRing ?? (() => true);
        calls.Devices = () => audio?.Invoke(HasEarpiece && Loudspeaker);
        if (platform.Camera is { } camera) calls.Camera = () => camera();
        if (platform.VideoDecoder is { } decoder) calls.VideoDecoder = () => decoder();
        calls.IncomingCall += OnIncoming;
        calls.CallChanged += OnChanged;
        clock = new Timer(_ => ui.Post(Refresh), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Whether the platform has audio, so calls can be made.</summary>
    public bool CanCall => audio is not null;

    /// <summary>Whether this device has a camera, so video calls can be made.</summary>
    public bool CanVideoCall => CanCall && platform.Camera is not null;

    /// <summary>Whether the loudspeaker is a choice.</summary>
    public bool HasEarpiece { get; }

    /// <summary>The call shown, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial VoiceCall? Current { get; set; }

    /// <summary>The other user's video, while the call runs.</summary>
    public VideoFeed RemoteVideo { get; } = new();

    /// <summary>This side's camera, small, while it sends.</summary>
    public VideoFeed LocalVideo { get; } = new();

    /// <summary>Whether this side sends its camera and has another one to switch to.</summary>
    [ObservableProperty]
    public partial bool CanSwitchCamera { get; set; }

    /// <summary>Whether this side's camera has a preview to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsVideo), nameof(ShowsFullScreen), nameof(MainVideo), nameof(ShowsCornerPreview))]
    public partial bool HasPreview { get; set; }

    /// <summary>Whether the call has video to show, the other side's or this side's own camera, as when only this side sends; the switch between video and chat shows then.</summary>
    public bool ShowsVideo => HasVideo || HasPreview;

    /// <summary>The video shown large: the other side's, or this side's own camera while the other side sends none.</summary>
    public VideoFeed MainVideo => HasVideo ? RemoteVideo : LocalVideo;

    /// <summary>Whether this side's camera shows small in the corner, over the other side's video.</summary>
    public bool ShowsCornerPreview => HasVideo && HasPreview;

    /// <summary>Whether this side's camera is turned off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraText))]
    public partial bool CameraOff { get; set; }

    /// <summary>Text of the camera button.</summary>
    public string CameraText => CameraOff ? "Camera on" : "Camera off";

    /// <summary>Turn this side's camera off or on; the preview hides while off.</summary>
    [RelayCommand]
    private void ToggleCamera()
    {
        // The call keeps it
        if (Current is not VoiceCall call) return;
        call.CameraOff = !call.CameraOff;
        CameraOff = call.CameraOff;
        if (!CameraOff) return;
        HasPreview = false;
        // Done
        LocalVideo.Clear();
    }

    /// <summary>Whether the other user's video has pictures to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsVideo), nameof(ShowsFullScreen), nameof(MainVideo), nameof(ShowsCornerPreview))]
    public partial bool HasVideo { get; set; }

    /// <summary>Whether the video call is wanted on the whole screen rather than as a bar over the rooms; it is when video starts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFullScreen), nameof(ScreenText))]
    public partial bool FullScreen { get; set; }

    /// <summary>Whether the call fills the screen: video shown, either side's, and wanted so; only the other side's video asks for it by itself.</summary>
    public bool ShowsFullScreen => ShowsVideo && FullScreen;

    /// <summary>Text of the button that switches between the full video and the chat.</summary>
    public string ScreenText => FullScreen ? "Chat" : "Video";

    /// <summary>Switch between the video on the whole screen and the chat under the call bar.</summary>
    [RelayCommand]
    private void ToggleScreen() => FullScreen = !FullScreen;

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

    /// <summary>Whether the ringing call has video and this device a camera, to answer with video.</summary>
    [ObservableProperty]
    public partial bool CanAnswerWithVideo { get; set; }

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
    public async Task PlaceCallAsync(string roomId, bool video = false)
    {
        // Permissions, then the call; without the camera, voice only
        Error = "";
        if (!await HasMicrophoneAsync()) return;
        if (video && platform.RequestCameraAsync is { } request && !await request())
        {
            Error = "No camera permission; calling with voice only.";
            video = false;
        }
        try
        {
            Show(await (video ? calls.PlaceVideoCallAsync(roomId) : calls.PlaceCallAsync(roomId)));
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

    /// <summary>Answer the ringing call with this device's camera too.</summary>
    [RelayCommand]
    private async Task AnswerWithVideoAsync()
    {
        // Microphone and camera, then the answer; without the camera, voice only
        if (Current is not VoiceCall call) return;
        if (!await HasMicrophoneAsync()) return;
        bool camera = platform.RequestCameraAsync is not { } request || await request();
        if (!camera) Error = "No camera permission; answering with voice only.";
        await RunAsync("Answering", () => camera ? call.AnswerWithVideoAsync() : call.AnswerAsync());
    }

    /// <summary>Whether this side sends its camera, to switch it.</summary>
    [ObservableProperty]
    public partial bool SendsVideo { get; set; }

    /// <summary>Switch the camera sent, e.g. from the front to the back.</summary>
    [RelayCommand]
    private void SwitchCamera() => Current?.SwitchCamera();

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
        if (Current is not null) Current.PictureReceived -= OnPicture;
        clock.Dispose();
        HoldPlatform(false, false);
    }

    /// <summary>Hold what the platform does for the call's state: keep it alive while active, ring while ringing, and turn the screen off near the ear while active on the earpiece, except in video calls, which are watched.</summary>
    private void HoldPlatform(bool ringingNow, bool active)
    {
        // Each started once and stopped once
        Hold(ref keptAlive, active, platform.KeepCallAlive is { } keep ? () => keep(PeerName) : null);
        Hold(ref ringing, ringingNow && mayRing(), platform.Ring is { } ring ? () => ring(PeerName) : null);
        bool video = HasVideo || Current?.SendsVideo == true || Current?.IsVideo == true;
        Hold(ref nearEar, active && !video && !(HasEarpiece && Loudspeaker), platform.ScreenOffNearEar);
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
        if (platform.Ring is null && mayRing() && platform.Notifier is INotifier notifier) _ = notifier.ShowAsync(call.IsVideo ? "Incoming video call" : "Incoming call", $"{nameOf(call.PeerUserId)} is calling");
    }

    /// <summary>Follow the shown call's changes; a new call replaces an ended one.</summary>
    private void OnChanged(VoiceCall call) => ui.Post(() =>
    {
        // The shown call, or one that replaces an ended one
        if (Current is not null && Current != call && Current.State != CallState.Ended) return;
        Show(call);
    });

    /// <summary>Follow the pictures of the shown call only, starting with none.</summary>
    partial void OnCurrentChanged(VoiceCall? oldValue, VoiceCall? newValue)
    {
        // Old pictures gone, new ones followed
        if (oldValue is not null)
        {
            oldValue.PictureReceived -= OnPicture;
            oldValue.PreviewReceived -= OnPreview;
        }
        if (newValue is not null)
        {
            newValue.PictureReceived += OnPicture;
            newValue.PreviewReceived += OnPreview;
        }
        LocalVideo.Clear();
        HasPreview = false;
        CameraOff = false;
        Interlocked.Exchange(ref pictured, 0);
        RemoteVideo.Clear();
        FullScreen = false;
        // Done
        HasVideo = false;
    }

    /// <summary>Show a preview picture of this side's camera; the first one shows the preview.</summary>
    private void OnPreview(VoiceCall call, VideoFrame picture)
    {
        // The shown call's, while it runs
        if (call != Current || call.State == CallState.Ended) return;
        LocalVideo.Push(picture);
        if (HasPreview) return;
        // Done
        ui.Post(() => { if (call == Current && !CameraOff) HasPreview = true; });
    }

    /// <summary>Show a picture of the call's video; the first one shows the video.</summary>
    private void OnPicture(VoiceCall call, VideoFrame picture)
    {
        // The shown call's, while it runs
        if (call != Current || call.State == CallState.Ended) return;
        RemoteVideo.Push(picture);
        if (Interlocked.Exchange(ref pictured, 1) == 1) return;
        // Done
        ui.Post(() => { if (call == Current) { HasVideo = true; FullScreen = true; HoldPlatform(IsRinging, IsActive); } });
    }

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
        CanAnswerWithVideo = IsRinging && call.IsVideo && platform.Camera is not null;
        SendsVideo = call.SendsVideo && call.State != CallState.Ended;
        CanSwitchCamera = SendsVideo && call.CanSwitchCamera;
        IsActive = call.State is not (CallState.Ringing or CallState.Ended);
        IsMuted = call.Muted;
        HoldPlatform(IsRinging, IsActive);
        if (call.State == CallState.Ended && HasVideo)
        {
            HasVideo = false;
            RemoteVideo.Clear();
        }
        if (call.State == CallState.Ended && HasPreview)
        {
            HasPreview = false;
            LocalVideo.Clear();
        }
        bool relayed = call.Path.Local?.Type == IceCandidateType.Relayed || call.Path.Remote?.Type == IceCandidateType.Relayed;
        // Done
        StateText = call.State switch
        {
            CallState.Ringing => call.IsVideo ? "Incoming video call" : "Incoming voice call",
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
