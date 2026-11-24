namespace Uniform.Chat.Terminal.Ui;

using Uniform.Matrix.Audio;
using Uniform.Matrix.Audio.Decoding;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Core;
using Uniform.Chat.Terminal.Localization;

// Audio messages: playing and stopping them from the message actions, with the progress in the status line.
public sealed partial class MainWindow
{
    /// <summary>How often the status line shows the position.</summary>
    private static readonly TimeSpan PlayingTick = TimeSpan.FromMilliseconds(500);

    private AudioClipPlayer? audioPlayer;
    private Timer? playingClock;
    private string? playingEventId;
    private string playingName = "";

    /// <summary>Return whether <paramref name="content"/> is an audio file to play here.</summary>
    private static bool IsPlayable(MessageContent? content) => content is { MessageType: MessageTypes.Audio or MessageTypes.File } && MxcUri.TryParse(content.MediaUrl, out _) && AudioFile.IsPlayable(content.Info?.Mimetype, content.Filename ?? content.Body);

    /// <summary>Return the message action that plays the audio message <paramref name="eventId"/>, or stops it when it plays.</summary>
    private (string Label, Action Run) PlayAction(MessageContent content, string eventId) => eventId == playingEventId
        ? (Strings.ButtonStopPlaying, () => _ = StopAudioAsync())
        : (Strings.ButtonPlay, () => _ = PlayAudioAsync(content, eventId));

    /// <summary>Download, decode and play an audio message, instead of any playing; the status line follows it.</summary>
    private async Task PlayAudioAsync(MessageContent content, string eventId)
    {
        // Stop the last, then the file from the media cache
        await StopAudioAsync();
        string name = content.Filename ?? content.Body ?? "audio";
        if (Client is not { } client)
        {
            ShowStatus(Strings.WaitUntilConnected);
            return;
        }
        ShowStatus(Texts.Format(Strings.LoadingName, name));
        try
        {
            StoredMedia media = await mediaCache.GetAsync(client, content);
            AudioClip clip = await AudioFile.DecodeFileAsync(mediaCache.GetFullPath(media));

            // Played on the media speaker of the settings' devices
            audioPlayer ??= CreateAudioPlayer();
            await audioPlayer.PlayAsync(clip);
            if (!audioPlayer.IsPlaying) return;
            playingEventId = eventId;
            playingName = name;
            playingClock ??= new Timer(_ => app.Invoke(ShowPlaying), null, Timeout.InfiniteTimeSpan, PlayingTick);
            playingClock.Change(TimeSpan.Zero, PlayingTick);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // On a file that cannot be had or played
            app.Invoke(() => ShowStatus(Texts.Format(Strings.CannotPlay, name, exception.Message)));
        }
    }

    /// <summary>Stop the audio message playing, if one plays.</summary>
    private async Task StopAudioAsync()
    {
        // The clock, then the player
        if (playingEventId is null) return;
        playingEventId = null;
        playingClock?.Change(Timeout.InfiniteTimeSpan, PlayingTick);
        if (audioPlayer is not null) await audioPlayer.StopAsync();
        // Done
        app.Invoke(() => ShowStatus(""));
    }

    /// <summary>Create the player on the media speaker of the devices the settings choose.</summary>
    private AudioClipPlayer CreateAudioPlayer()
    {
        // The speaker opens per play
        AudioClipPlayer player = new(() => OpenCallDevices()?.OpenMediaSpeaker() ?? throw new PlatformNotSupportedException(Strings.AudioUnsupported));
        player.Ended += ended => app.Invoke(() => _ = StopAudioAsync());
        player.Failed += exception => app.Invoke(() =>
        {
            playingClock?.Change(Timeout.InfiniteTimeSpan, PlayingTick);
            playingEventId = null;
            ShowStatus(Texts.Format(Strings.CannotPlay, playingName, exception.Message));
        });
        // Return the player
        return player;
    }

    /// <summary>Show the position of the audio message playing in the status line.</summary>
    private void ShowPlaying()
    {
        // While one plays
        if (playingEventId is null || audioPlayer?.Clip is not AudioClip clip) return;
        ShowStatus(Texts.Format(Strings.PlayingStatus, playingName, Clock(audioPlayer.Position), Clock(clip.Duration)));
    }

    /// <summary>Return a time as <c>m:ss</c>, or <c>h:mm:ss</c> from an hour.</summary>
    private static string Clock(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
}
