namespace Uniform.Chat.Avalonia.ViewModels;

using System.Collections.Specialized;
using Uniform.Chat.Avalonia.Localization;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.Video;

/// <summary>Plays the audio and video messages of the open room, one at a time: downloads the file, opens it with an <see cref="IMediaPlayback"/>, shows the position and a video's pictures on the message, and remembers which audio messages were listened to.</summary>
/// <remarks>The message list makes new items when a room opens again or its events are decrypted; the player follows the item of the message playing by its event ID, and stops when it goes, e.g. deleted. A message played to its end keeps its playback, so playing it again needs no new decoding.</remarks>
public sealed class MediaMessagePlayer : IAsyncDisposable
{
    /// <summary>How often the position shown is updated.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

    private readonly Func<MessageViewModel, IMediaPlayback> create;
    private readonly IUiDispatcher ui;
    private readonly Func<MessageViewModel, Task<string?>> download;
    private readonly Func<string, Task> markListened;
    private readonly HashSet<string> listened = [];
    private readonly Timer clock;
    private MessageViewModel? current;
    private IMediaPlayback? playback;
    private string? playbackEventId;

    /// <summary>Create a player.</summary>
    /// <param name="create">Makes the playback of a message: audio, or video.</param>
    /// <param name="ui">Dispatcher of the user interface thread.</param>
    /// <param name="download">Returns the file of a message from the media cache, downloading it; null when it failed, which it tells itself.</param>
    /// <param name="markListened">Saves that the audio message of an event ID was listened to.</param>
    public MediaMessagePlayer(Func<MessageViewModel, IMediaPlayback> create, IUiDispatcher ui, Func<MessageViewModel, Task<string?>> download, Func<string, Task> markListened)
    {
        // The clock that shows the position
        this.create = create;
        this.ui = ui;
        this.download = download;
        this.markListened = markListened;
        clock = new Timer(_ => ui.Post(ShowPosition), null, Timeout.InfiniteTimeSpan, Tick);
    }

    /// <summary>Raised on the UI thread with a failure to tell, e.g. a file that cannot be played.</summary>
    public event Action<string>? Notice;

    /// <summary>The message playing or paused; null for none.</summary>
    public MessageViewModel? Current => current;

    /// <summary>Remember the audio messages listened to before, to mark them; call once at the start.</summary>
    public void Remember(IEnumerable<string> eventIds) => listened.UnionWith(eventIds);

    /// <summary>Return whether the audio message of <paramref name="eventId"/> was listened to.</summary>
    public bool IsListened(string eventId) => listened.Contains(eventId);

    /// <summary>Follow a <paramref name="change"/> of the open room's timeline, to keep the playing message's item when the list makes it anew, and stop when it is removed or deleted; call on the UI thread.</summary>
    /// <remarks>A list cleared to be filled again, as when the room opens again, keeps playing; <see cref="KeepIfShown"/> tells after the refill.</remarks>
    public void Follow(NotifyCollectionChangedEventArgs change)
    {
        // A new item of the message playing takes over, unless deleted
        if (current?.EventId is not string playing) return;
        if (change.NewItems?.OfType<MessageViewModel>().FirstOrDefault(m => m.EventId == playing) is MessageViewModel renewed)
        {
            if (renewed.HasPlayer) Adopt(renewed);
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
        if (message == current && playback is IMediaPlayback open)
        {
            if (open.IsPlaying) await PauseAsync();
            else await ResumeAsync();
            return;
        }

        // Another one: stop the last, then open it, unless kept from playing it before
        if (message.EventId is not string eventId) return;
        await StopAsync(keep: eventId);
        current = message;
        message.IsLoadingMedia = true;
        try
        {
            if (playback is null)
            {
                if (await download(message) is not string path || current != message) return;
                IMediaPlayback opened = create(message);
                try { await opened.OpenAsync(path); }
                catch
                {
                    await opened.DisposeAsync();
                    throw;
                }
                Attach(opened, eventId);
            }
            if (current != message) return;

            // From where the message shows, then the clock
            message.PlayLength = playback!.Duration.TotalSeconds;
            message.Seeked = OnSeeked;
            if (message.PlayPosition > 0 && message.PlayPosition < message.PlayLength - 0.5) await playback.SeekAsync(TimeSpan.FromSeconds(message.PlayPosition));
            await playback.PlayAsync();
            message.IsPlaying = playback.IsPlaying;
            if (!playback.IsPlaying) return;
            clock.Change(TimeSpan.Zero, Tick);
            if (message.IsPlayable) await MarkListenedAsync(message, eventId);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidDataException or IOException)
        {
            // On a file that is not one to play here
            Notice?.Invoke(Translations.Format(Strings.CannotPlay, message.FileName, exception.Message));
            if (current == message) current = null;
        }
        finally
        {
            message.IsLoadingMedia = false;
        }
    }

    /// <summary>Stop, and leave the message at its start; a video keeps its last picture.</summary>
    public Task StopAsync() => StopAsync(keep: null);

    /// <summary>Stop and close the playback.</summary>
    public async ValueTask DisposeAsync()
    {
        // Stopped, the clock too
        await StopAsync();
        await clock.DisposeAsync();
    }

    /// <summary>Stop; keep the playback when it is of <paramref name="keep"/>, to play that message again, else close it.</summary>
    private async Task StopAsync(string? keep)
    {
        // The clock, the playback
        clock.Change(Timeout.InfiniteTimeSpan, Tick);
        if (playback is IMediaPlayback open)
        {
            if (playbackEventId == keep)
            {
                if (open.IsPlaying) await open.PauseAsync();
                await open.SeekAsync(TimeSpan.Zero);
            }
            else
            {
                playback = null;
                playbackEventId = null;
                await open.DisposeAsync();
            }
        }

        // Done: the message shown stopped
        if (current is not MessageViewModel stopped) return;
        current = null;
        stopped.IsPlaying = false;
        stopped.Seeked = null;
        stopped.ShowPosition(0);
    }

    /// <summary>Take an opened playback for the message of <paramref name="eventId"/>: its pictures to the message's video, its end and failures to the UI thread.</summary>
    private void Attach(IMediaPlayback opened, string eventId)
    {
        // Pictures go to the feed the message shows, which a renewed item keeps
        VideoFeed? feed = current!.IsVideo ? current.Video ??= new VideoFeed() : null;
        if (feed is not null) opened.PictureReady += feed.Push;
        opened.Ended += () => ui.Post(() => OnEnded(opened));
        opened.Failed += e => ui.Post(() => OnFailed(opened, e));
        playback = opened;
        // Done
        playbackEventId = eventId;
    }

    /// <summary>Pause, keeping the position.</summary>
    private async Task PauseAsync()
    {
        // Stopped where it is
        clock.Change(Timeout.InfiniteTimeSpan, Tick);
        await playback!.PauseAsync();
        ShowPosition();
        // Done
        if (current is not null) current.IsPlaying = false;
    }

    /// <summary>Resume from the position.</summary>
    private async Task ResumeAsync()
    {
        // From where it paused, or where the user moved it
        await playback!.PlayAsync();
        if (current is null) return;
        current.IsPlaying = playback.IsPlaying;
        // Done
        if (playback.IsPlaying) clock.Change(TimeSpan.Zero, Tick);
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

    /// <summary>Move the playback to the position the user chose on the message playing or paused.</summary>
    private void OnSeeked(MessageViewModel message, double seconds)
    {
        // The current message only
        if (message == current && playback is IMediaPlayback open) _ = open.SeekAsync(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>Take over a new item of the message playing: its state, its video, and the seeking.</summary>
    private void Adopt(MessageViewModel renewed)
    {
        // State copied from the old item
        MessageViewModel old = current!;
        old.Seeked = null;
        renewed.PlayLength = old.PlayLength;
        renewed.ShowPosition(old.PlayPosition);
        renewed.IsPlaying = old.IsPlaying;
        renewed.IsListened = old.IsListened;
        renewed.Video = old.Video;
        renewed.Seeked = OnSeeked;
        // Done
        current = renewed;
    }

    /// <summary>Show the playback's position on the message.</summary>
    private void ShowPosition()
    {
        // While one is open
        if (current is null || playback is null) return;
        current.ShowPosition(playback.Position.TotalSeconds);
    }

    /// <summary>Show the message played to its end: stopped, at its start, the playback kept to play again.</summary>
    private void OnEnded(IMediaPlayback ended)
    {
        // The current playback only
        if (ended != playback) return;
        clock.Change(Timeout.InfiniteTimeSpan, Tick);
        if (current is null) return;
        current.IsPlaying = false;
        current.ShowPosition(0);
        current.Seeked = null;
        // Done
        current = null;
    }

    /// <summary>Show a playback that failed, e.g. its speaker.</summary>
    private void OnFailed(IMediaPlayback failed, Exception exception)
    {
        // The current playback only, stopped and told
        if (failed != playback) return;
        clock.Change(Timeout.InfiniteTimeSpan, Tick);
        if (current is not null) current.IsPlaying = false;
        // Done
        Notice?.Invoke(Translations.Format(Strings.CannotPlay, current?.FileName, exception.Message));
    }
}
