namespace Uniform.Chat.Avalonia.ViewModels;

using System.Collections.Specialized;
using Uniform.Chat.Avalonia.Localization;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Audio.Decoding;

/// <summary>Plays the audio messages of the open room, one at a time: downloads and decodes the file, follows the position on the message, and remembers which messages were listened to.</summary>
/// <remarks>The message list makes new items when a room opens again or its events are decrypted; the player follows the item of the message playing by its event ID, and stops when it goes, e.g. deleted.</remarks>
public sealed class AudioMessagePlayer : IAsyncDisposable
{
    /// <summary>How often the position shown is updated.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

    private readonly AudioClipPlayer player;
    private readonly IUiDispatcher ui;
    private readonly Func<MessageViewModel, Task<string?>> download;
    private readonly Func<string, Task> markListened;
    private readonly HashSet<string> listened = [];
    private readonly Timer clock;
    private MessageViewModel? current;
    private (string EventId, AudioClip Clip)? decoded;

    /// <summary>Create a player on <paramref name="openSpeaker"/>.</summary>
    /// <param name="openSpeaker">Opens the media speaker.</param>
    /// <param name="ui">Dispatcher of the user interface thread.</param>
    /// <param name="download">Returns the file of a message from the media cache, downloading it; null when it failed, which it tells itself.</param>
    /// <param name="markListened">Saves that the message of an event ID was listened to.</param>
    public AudioMessagePlayer(Func<NAudio.Wave.IWavePlayer> openSpeaker, IUiDispatcher ui, Func<MessageViewModel, Task<string?>> download, Func<string, Task> markListened)
    {
        // The player, and the clock that shows its position
        this.ui = ui;
        this.download = download;
        this.markListened = markListened;
        player = new AudioClipPlayer(openSpeaker);
        player.Ended += _ => ui.Post(OnEnded);
        player.Failed += e => ui.Post(() => OnFailed(e));
        clock = new Timer(_ => ui.Post(ShowPosition), null, Timeout.InfiniteTimeSpan, Tick);
    }

    /// <summary>Raised on the UI thread with a failure to tell, e.g. a file that cannot be played.</summary>
    public event Action<string>? Notice;

    /// <summary>The message playing or paused; null for none.</summary>
    public MessageViewModel? Current => current;

    /// <summary>Remember the messages listened to before, to mark them; call once at the start.</summary>
    public void Remember(IEnumerable<string> eventIds) => listened.UnionWith(eventIds);

    /// <summary>Return whether the message of <paramref name="eventId"/> was listened to.</summary>
    public bool IsListened(string eventId) => listened.Contains(eventId);

    /// <summary>Follow a <paramref name="change"/> of the open room's timeline, to keep the playing message's item when the list makes it anew, and stop when it is removed or deleted; call on the UI thread.</summary>
    /// <remarks>A list cleared to be filled again, as when the room opens again, keeps playing; <see cref="KeepIfShown"/> tells after the refill.</remarks>
    public void Follow(NotifyCollectionChangedEventArgs change)
    {
        // A new item of the message playing takes over, unless deleted
        if (current?.EventId is not string playing) return;
        if (change.NewItems?.OfType<MessageViewModel>().FirstOrDefault(m => m.EventId == playing) is MessageViewModel renewed)
        {
            if (renewed.IsPlayable) Adopt(renewed);
            else _ = StopAsync();
            return;
        }
        // Done: stopped when its item left without a new one
        if (change.Action != NotifyCollectionChangedAction.Reset && change.OldItems?.Contains(current) == true) _ = StopAsync();
    }

    /// <summary>Stop unless the message playing is among <paramref name="items"/>, e.g. after the list was filled again without it.</summary>
    public void KeepIfShown(IEnumerable<MessageViewModel> items)
    {
        // Shown, or stopped
        if (current is MessageViewModel playing && !items.Contains(playing)) _ = StopAsync();
    }

    /// <summary>Play <paramref name="message"/>, or pause it when it plays, or resume it when paused.</summary>
    public async Task ToggleAsync(MessageViewModel message)
    {
        // The one playing pauses, the one paused resumes
        if (message == current)
        {
            if (player.IsPlaying) await PauseAsync();
            else await ResumeAsync();
            return;
        }

        // Another one: stop the last, then download, decode and play
        await StopAsync();
        if (message.EventId is not string eventId) return;
        current = message;
        message.IsLoadingAudio = true;
        try
        {
            AudioClip? clip = decoded is { } known && known.EventId == eventId ? known.Clip : null;
            if (clip is null)
            {
                if (await download(message) is not string path) return;
                clip = await AudioFile.DecodeFileAsync(path);
                decoded = (eventId, clip);
            }
            if (current != message) return;
            message.PlayLength = clip.Duration.TotalSeconds;
            message.Seeked = OnSeeked;
            await player.PlayAsync(clip, TimeSpan.FromSeconds(message.PlayPosition < message.PlayLength - 0.5 ? message.PlayPosition : 0));
            message.IsPlaying = player.IsPlaying;
            if (!player.IsPlaying) return;
            clock.Change(TimeSpan.Zero, Tick);
            await MarkListenedAsync(message, eventId);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidDataException or IOException)
        {
            // On a file that is not one to play
            Notice?.Invoke(Translations.Format(Strings.CannotPlay, message.FileName, exception.Message));
            if (current == message) current = null;
        }
        finally
        {
            message.IsLoadingAudio = false;
        }
    }

    /// <summary>Stop, and leave the message at its start.</summary>
    public async Task StopAsync()
    {
        // The player, then the message shown stopped
        clock.Change(Timeout.InfiniteTimeSpan, Tick);
        await player.StopAsync();
        if (current is not MessageViewModel stopped) return;
        current = null;
        stopped.IsPlaying = false;
        stopped.Seeked = null;
        // Done
        stopped.ShowPosition(0);
    }

    /// <summary>Stop and close the speaker.</summary>
    public async ValueTask DisposeAsync()
    {
        // Stopped, the clock too
        await StopAsync();
        await clock.DisposeAsync();
        await player.DisposeAsync();
    }

    /// <summary>Pause, keeping the position.</summary>
    private async Task PauseAsync()
    {
        // Stopped where it is
        clock.Change(Timeout.InfiniteTimeSpan, Tick);
        await player.PauseAsync();
        ShowPosition();
        // Done
        if (current is not null) current.IsPlaying = false;
    }

    /// <summary>Resume from the position.</summary>
    private async Task ResumeAsync()
    {
        // From where it paused, or where the user moved it
        await player.ResumeAsync();
        if (current is null) return;
        current.IsPlaying = player.IsPlaying;
        // Done
        if (player.IsPlaying) clock.Change(TimeSpan.Zero, Tick);
    }

    /// <summary>Mark the message listened to, and save it once.</summary>
    private async Task MarkListenedAsync(MessageViewModel message, string eventId)
    {
        // Shown now, saved the first time
        message.IsListened = true;
        if (!listened.Add(eventId)) return;
        try { await markListened(eventId); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Not saved: marked for this run only
        }
    }

    /// <summary>Move the player to the position the user chose on the message playing or paused.</summary>
    private void OnSeeked(MessageViewModel message, double seconds)
    {
        // The current message only
        if (message == current) player.Seek(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>Take over a new item of the message playing: its state, and the seeking.</summary>
    private void Adopt(MessageViewModel renewed)
    {
        // State copied from the old item
        MessageViewModel old = current!;
        old.Seeked = null;
        renewed.PlayLength = old.PlayLength;
        renewed.ShowPosition(old.PlayPosition);
        renewed.IsPlaying = old.IsPlaying;
        renewed.IsListened = true;
        renewed.Seeked = OnSeeked;
        // Done
        current = renewed;
    }

    /// <summary>Show the player's position on the message.</summary>
    private void ShowPosition()
    {
        // While it has a clip
        if (current is null || player.Clip is null) return;
        current.ShowPosition(player.Position.TotalSeconds);
    }

    /// <summary>Show the message played to its end: stopped, at its start.</summary>
    private void OnEnded()
    {
        // The clock stops; the clip stays decoded for playing again
        clock.Change(Timeout.InfiniteTimeSpan, Tick);
        if (current is null) return;
        current.IsPlaying = false;
        current.ShowPosition(0);
        current.Seeked = null;
        // Done
        current = null;
        _ = player.StopAsync();
    }

    /// <summary>Show a speaker that failed.</summary>
    private void OnFailed(Exception exception)
    {
        // Stopped, and told
        clock.Change(Timeout.InfiniteTimeSpan, Tick);
        if (current is not null) current.IsPlaying = false;
        // Done
        Notice?.Invoke(Translations.Format(Strings.CannotPlay, current?.FileName, exception.Message));
    }
}
