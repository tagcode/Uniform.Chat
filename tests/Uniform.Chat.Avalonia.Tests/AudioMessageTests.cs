namespace Uniform.Chat.Avalonia.Tests;

using global::Avalonia.Headless.XUnit;
using NAudio.Wave;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using static Uniform.Chat.Avalonia.Tests.ChatFlowTests;

/// <summary>Audio messages play in the chat: a player with its position, a mark once listened, kept across runs, and a notice for a file that cannot be played.</summary>
public class AudioMessageTests
{
    /// <summary>Bob's voice message, Ogg Opus as Element sends, plays on Alice's media speaker to its end; it is marked listened, also when the room opens again.</summary>
    [AvaloniaFact]
    public async Task PlayVoiceMessage()
    {
        // Arrange: Bob's voice message and a broken one in a room with Alice
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient bob = await server.RegisterAsync("bob");
        MatrixClient alice = await server.RegisterAsync("alice");
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Voice", Preset = RoomPresets.PublicChat });
        await alice.JoinRoomAsync(roomId);
        await using (Stream voice = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Resources", "tone_voice_mono.ogg"))) await bob.SendFileAsync(roomId, voice, "voice.ogg", "audio/ogg");
        await bob.SendFileAsync(roomId, new MemoryStream(new byte[2000]), "broken.mp3", "audio/mpeg");
        PlayingDevices devices = new();
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName) with { Audio = _ => devices }, new AvaloniaUiDispatcher());

        try
        {
            ChatViewModel chat = await SignInAsync(model, alice.UserId!);
            await WaitUntilAsync(() => chat.Rooms.Any(r => r.RoomId == roomId), "the room");
            RoomItemViewModel room = chat.Rooms.First(r => r.RoomId == roomId);
            chat.SelectedRoom = room;
            await WaitUntilAsync(() => chat.Messages.Items.Count(m => m.IsPlayable) == 2, "both audio messages");
            MessageViewModel voice = chat.Messages.Items.First(m => m.FileName == "voice.ogg");
            Assert.False(voice.IsListened);

            // Act: play
            await chat.TogglePlayAsync(voice);

            // Assert: plays on the media speaker, with its length and position, and is listened
            Assert.True(voice.IsPlaying);
            Assert.Equal(1.2, voice.PlayLength, 2);
            Assert.True(voice.IsListened);
            Assert.Equal(1, devices.MediaSpeakers);
            await WaitUntilAsync(() => voice.PlayPosition > 0.2, "the position to move");
            Assert.StartsWith("0:00 / 0:01", voice.PlayTimeText);
            await CaptureAsync(model, "audio-playing.png");

            // Assert: played to the end, stopped at the start
            await WaitUntilAsync(() => !voice.IsPlaying, "the end");
            Assert.Equal(0, voice.PlayPosition);

            // Act and assert: the broken one only tells
            await chat.TogglePlayAsync(chat.Messages.Items.First(m => m.FileName == "broken.mp3"));
            Assert.StartsWith("Cannot play broken.mp3", chat.Notice);

            // Done: the room opened again shows it listened, from the store
            chat.SelectedRoom = null;
            chat.SelectedRoom = room;
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.FileName == "voice.ogg" && m != voice), "the room again");
            Assert.True(chat.Messages.Items.First(m => m.FileName == "voice.ogg").IsListened);
            await CaptureAsync(model, "audio-listened.png", width: 420, height: 760);
            Assert.Contains(voice.EventId!, await model.Connection!.Store.LoadListenedAsync());
        }
        finally
        {
            await model.StopAsync();
        }
    }

    /// <summary>Devices whose media speaker plays in real time into nothing; calls are not made.</summary>
    private sealed class PlayingDevices : IAudioDevices
    {
        private int mediaSpeakers;

        /// <summary>How many media speakers were opened.</summary>
        public int MediaSpeakers => Volatile.Read(ref mediaSpeakers);
        /// <summary>Not used.</summary>
        public IWaveIn OpenMicrophone() => throw new NotSupportedException();
        /// <summary>Not used.</summary>
        public IWavePlayer OpenSpeaker() => throw new NotSupportedException();
        /// <summary>A speaker that pulls in real time.</summary>
        public IWavePlayer OpenMediaSpeaker()
        {
            Interlocked.Increment(ref mediaSpeakers);
            return new PullingSpeaker();
        }
        /// <summary>None.</summary>
        public IReadOnlyList<AudioDevice> ListMicrophones() => [];
        /// <summary>None.</summary>
        public IReadOnlyList<AudioDevice> ListSpeakers() => [];
    }

    /// <summary>Reads 20 ms at a time from its source, as a device would.</summary>
    private sealed class PullingSpeaker : IWavePlayer
    {
        private IWaveProvider? source;
        private Timer? timer;

        /// <summary>Playing or stopped.</summary>
        public PlaybackState PlaybackState { get; private set; }
        /// <summary>Unused.</summary>
        public float Volume { get; set; } = 1;
        /// <summary>The source's format.</summary>
        public WaveFormat OutputWaveFormat => source!.WaveFormat;
        /// <summary>Not raised.</summary>
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        /// <summary>Keep the source.</summary>
        public void Init(IWaveProvider waveProvider) => source = waveProvider;
        /// <summary>Pull every 20 ms.</summary>
        public void Play()
        {
            PlaybackState = PlaybackState.Playing;
            timer = new Timer(_ => { lock (this) if (PlaybackState == PlaybackState.Playing) source?.Read(new byte[960 * 4]); }, null, 0, 20);
        }
        /// <summary>Stop.</summary>
        public void Pause() => Stop();
        /// <summary>Stop.</summary>
        public void Stop()
        {
            lock (this) PlaybackState = PlaybackState.Stopped;
            timer?.Dispose();
        }
        /// <summary>Stop.</summary>
        public void Dispose() => Stop();
    }
}
