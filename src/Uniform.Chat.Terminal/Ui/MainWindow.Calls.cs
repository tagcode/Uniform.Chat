namespace Uniform.Chat.Terminal.Ui;

using Microsoft.Extensions.Configuration;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Calls;
using Uniform.Chat.Terminal.Configuration;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.WebRtc.Ice;
using Uniform.Chat.Terminal.Localization;

// Voice calls: placing, answering and ending them, and their state in the status bar.
public sealed partial class MainWindow
{
    /// <summary>Open the microphone and speaker the settings choose, for calls.</summary>
    private IAudioDevices? OpenCallDevices()
    {
        // The audio options as they are now
        AudioOptions audio = configuration.GetSection(AudioOptions.Section).Get<AudioOptions>() ?? new AudioOptions();
        // Return the platform's devices
        return AudioDevices.ForThisPlatform(audio.Microphone, audio.Speaker);
    }

    /// <summary>Call the other member of the open room.</summary>
    private void PlaceCall()
    {
        // A room with one other member
        if (currentRoomId is not string roomId)
        {
            Dialogs.Error(app, Strings.Call, Strings.OpenDirectChatFirst);
            return;
        }
        // Done
        _ = RunCallActionAsync(Strings.Calling, () => calls.PlaceCallAsync(roomId));
    }

    /// <summary>Answer the ringing call, the open room's first.</summary>
    private void AnswerCall()
    {
        // The ringing call
        if (FindCall(CallState.Ringing) is not VoiceCall call)
        {
            ShowStatus(Strings.NoCallRinging);
            return;
        }
        // Done
        _ = RunCallActionAsync(Strings.Answering, () => call.AnswerAsync());
    }

    /// <summary>Decline the ringing call, the open room's first.</summary>
    private void RejectCall()
    {
        // The ringing call
        if (FindCall(CallState.Ringing) is not VoiceCall call)
        {
            ShowStatus(Strings.NoCallRinging);
            return;
        }
        // Done
        _ = RunCallActionAsync(Strings.RejectingCall, () => call.RejectAsync());
    }

    /// <summary>End the call, the open room's first.</summary>
    private void HangupCall()
    {
        // Any call not ended
        if (FindCall(null) is not VoiceCall call)
        {
            ShowStatus(Strings.NoCallToHangUp);
            return;
        }
        // Done
        _ = RunCallActionAsync(Strings.HangingUp, () => call.IsIncoming && call.State == CallState.Ringing ? call.RejectAsync() : call.HangupAsync());
    }

    /// <summary>Mute or unmute the microphone of the call.</summary>
    private void ToggleMute()
    {
        // Any call not ended
        if (FindCall(null) is not VoiceCall call)
        {
            ShowStatus(Strings.NoCallToMute);
            return;
        }
        // Done
        call.Muted = !call.Muted;
    }

    /// <summary>Return the call in the open room, else any, in <paramref name="state"/> or any state but ended.</summary>
    private VoiceCall? FindCall(CallState? state)
    {
        // The open room's first
        IEnumerable<VoiceCall> matching = calls.ActiveCalls.Where(c => state is null || c.State == state);
        // Return it
        return matching.OrderByDescending(c => c.RoomId == currentRoomId).FirstOrDefault();
    }

    /// <summary>Run a call action, showing what failed.</summary>
    private async Task RunCallActionAsync(string what, Func<Task> action)
    {
        // Errors to a dialog
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            app.Invoke(() => Dialogs.Error(app, Strings.Call, Texts.Format(Strings.ActionFailed, what, ex.Message)));
        }
    }

    /// <summary>Show a call's state in the status bar.</summary>
    private void OnCallChanged(VoiceCall call)
    {
        // What it is doing
        string peer = call.PeerUserId;
        string muted = call.Muted ? Strings.Muted : "";
        string text = call.State switch
        {
            CallState.Ringing => Texts.Format(Strings.CallRinging, peer),
            CallState.Inviting => Texts.Format(Strings.CallInviting, peer),
            CallState.Connecting => Texts.Format(Strings.CallConnecting, peer),
            CallState.Connected => Texts.Format(Strings.CallConnected, peer, (call.Path.Local?.Type == IceCandidateType.Relayed || call.Path.Remote?.Type == IceCandidateType.Relayed ? Strings.Relayed : ""), muted),
            CallState.Reconnecting => Texts.Format(Strings.CallReconnecting, peer),
            _ => Texts.Format(Strings.CallEnded, peer, CallEndText(call)),
        };
        // Done
        ShowStatus(text);
    }

    /// <summary>Return why a call ended, for people.</summary>
    private static string CallEndText(VoiceCall call) => call.EndReason switch
    {
        "user_hangup" => call.EndedByPeer ? Strings.EndTheyHungUp : Strings.EndYouHungUp,
        "rejected" => call.EndedByPeer ? Strings.EndDeclined : Strings.EndYouDeclined,
        "invite_timeout" => call.IsIncoming ? Strings.EndMissed : Strings.EndNoAnswer,
        "ice_failed" => Strings.EndNoConnection,
        "answered_elsewhere" => Strings.EndAnsweredElsewhere,
        "rejected_elsewhere" => Strings.EndDeclinedElsewhere,
        "replaced" => Strings.EndCalledAgain,
        string reason => reason,
        null => Strings.EndEnded,
    };
}
