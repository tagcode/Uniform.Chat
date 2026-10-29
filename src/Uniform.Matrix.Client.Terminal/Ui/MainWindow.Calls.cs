namespace Uniform.Matrix.Client.Terminal.Ui;

using Microsoft.Extensions.Configuration;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Calls;
using Uniform.Matrix.Client.Terminal.Configuration;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.WebRtc.Ice;

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
            Dialogs.Error(app, "Call", "Open a direct chat first.");
            return;
        }
        // Done
        _ = RunCallActionAsync("Calling", () => calls.PlaceCallAsync(roomId));
    }

    /// <summary>Answer the ringing call, the open room's first.</summary>
    private void AnswerCall()
    {
        // The ringing call
        if (FindCall(CallState.Ringing) is not VoiceCall call)
        {
            ShowStatus("No call is ringing.");
            return;
        }
        // Done
        _ = RunCallActionAsync("Answering", () => call.AnswerAsync());
    }

    /// <summary>Decline the ringing call, the open room's first.</summary>
    private void RejectCall()
    {
        // The ringing call
        if (FindCall(CallState.Ringing) is not VoiceCall call)
        {
            ShowStatus("No call is ringing.");
            return;
        }
        // Done
        _ = RunCallActionAsync("Rejecting", () => call.RejectAsync());
    }

    /// <summary>End the call, the open room's first.</summary>
    private void HangupCall()
    {
        // Any call not ended
        if (FindCall(null) is not VoiceCall call)
        {
            ShowStatus("No call to hang up.");
            return;
        }
        // Done
        _ = RunCallActionAsync("Hanging up", () => call.IsIncoming && call.State == CallState.Ringing ? call.RejectAsync() : call.HangupAsync());
    }

    /// <summary>Mute or unmute the microphone of the call.</summary>
    private void ToggleMute()
    {
        // Any call not ended
        if (FindCall(null) is not VoiceCall call)
        {
            ShowStatus("No call to mute.");
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
            app.Invoke(() => Dialogs.Error(app, "Call", $"{what} failed: {ex.Message}"));
        }
    }

    /// <summary>Show a call's state in the status bar.</summary>
    private void OnCallChanged(VoiceCall call)
    {
        // What it is doing
        string peer = call.PeerUserId;
        string muted = call.Muted ? ", muted" : "";
        string text = call.State switch
        {
            CallState.Ringing => $"Call: {peer} is calling: /answer or /reject",
            CallState.Inviting => $"Call: Calling {peer}…  /hangup to cancel",
            CallState.Connecting => $"Call: Connecting to {peer}…",
            CallState.Connected => $"Call: In a call with {peer}{(call.Path.Local?.Type == IceCandidateType.Relayed || call.Path.Remote?.Type == IceCandidateType.Relayed ? ", relayed" : "")}{muted}  /mute /hangup",
            CallState.Reconnecting => $"Call: Reconnecting to {peer}…",
            _ => $"Call: Call with {peer} ended: {CallEndText(call)}",
        };
        // Done
        ShowStatus(text);
    }

    /// <summary>Return why a call ended, for people.</summary>
    private static string CallEndText(VoiceCall call) => call.EndReason switch
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
}
