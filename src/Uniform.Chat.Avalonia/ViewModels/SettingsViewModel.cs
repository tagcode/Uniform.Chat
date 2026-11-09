namespace Uniform.Chat.Avalonia.ViewModels;

using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Matrix.Audio;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.Hosting;

/// <summary>Colour theme of the app.</summary>
public enum AppTheme
{
    /// <summary>Follow the system's light or dark mode.</summary>
    System,
    /// <summary>Always light.</summary>
    Light,
    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>Appearance and notification settings, kept in the <c>Ui</c> section of the app's <c>settings.json</c> and saved on every change.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    /// <summary>Section of the settings file.</summary>
    public const string Section = "Ui";
    private readonly string path;
    private readonly GuiPlatform? platform;
    private bool loading;

    /// <summary>Load the settings of <paramref name="applicationName"/>; <paramref name="canNotify"/> tells whether the platform shows notifications, <paramref name="platform"/>, when given, offers its audio for the audio test and its push distributors, and <paramref name="dispatcher"/> brings the distributors' answers to the user interface thread.</summary>
    public SettingsViewModel(string applicationName, bool canNotify, GuiPlatform? platform = null, IUiDispatcher? dispatcher = null)
    {
        // Missing values keep their defaults
        path = SettingsStore.DefaultPath(applicationName);
        CanNotify = canNotify;
        this.platform = platform;
        JsonObject ui = SettingsFile.Read(path)[Section] as JsonObject ?? [];
        loading = true;
        if (ui[nameof(Theme)] is JsonValue themeValue && themeValue.TryGetValue(out string? themeName) && Enum.TryParse(themeName, out AppTheme theme)) Theme = theme;
        if (ui[nameof(TextSize)] is JsonValue sizeValue && sizeValue.TryGetValue(out double size) && size is >= 10 and <= 24) TextSize = size;
        NotifyDirectMessages = ReadBool(ui, nameof(NotifyDirectMessages), NotifyDirectMessages);
        NotifyMentions = ReadBool(ui, nameof(NotifyMentions), NotifyMentions);
        NotifyInvites = ReadBool(ui, nameof(NotifyInvites), NotifyInvites);
        NotifyCalls = ReadBool(ui, nameof(NotifyCalls), NotifyCalls);
        loading = false;

        // Push choices follow the distributor's answers
        this.dispatcher = dispatcher;
        if (platform?.Push is IPushService push) push.Changed += () => Post(RefreshPush);
        RefreshPush();
    }

    private readonly IUiDispatcher? dispatcher;

    /// <summary>Whether the platform can receive push messages, so their choice is offered.</summary>
    public bool CanPush => platform?.Push is not null;

    /// <summary>How the app hears of new messages in the background: the app's own connection first, then each UnifiedPush distributor installed.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> PushChoices { get; set; } = [];

    /// <summary>The chosen entry of <see cref="PushChoices"/>; choosing a distributor registers with it, choosing the first unregisters.</summary>
    [ObservableProperty]
    public partial int PushChoiceIndex { get; set; }

    /// <summary>Where push stands, e.g. <c>Waiting for ntfy…</c>.</summary>
    [ObservableProperty]
    public partial string PushStatus { get; set; } = "";

    /// <summary>Distributors behind <see cref="PushChoices"/> after the first.</summary>
    private IReadOnlyList<PushDistributor> distributors = [];

    /// <summary>Register with the chosen distributor, or unregister.</summary>
    partial void OnPushChoiceIndexChanged(int value)
    {
        // Not while showing what is chosen already
        if (loading || platform?.Push is not IPushService push) return;
        if (value <= 0)
        {
            if (push.Distributor is not null) push.Unregister();
        }
        else if (value - 1 < distributors.Count && distributors[value - 1].Id != push.Distributor) push.Register(distributors[value - 1].Id);
        RefreshPush();
    }

    /// <summary>Show the distributors installed now, the chosen one, and where push stands.</summary>
    [RelayCommand]
    private void RefreshPush()
    {
        // Choices
        if (platform?.Push is not IPushService push) return;
        distributors = push.GetDistributors();
        loading = true;
        PushChoices = ["Own background connection", .. distributors.Select(d => $"UnifiedPush: {d.Name}")];
        int chosen = distributors.ToList().FindIndex(d => d.Id == push.Distributor);
        PushChoiceIndex = chosen + 1;
        loading = false;

        // Status
        string? name = distributors.FirstOrDefault(d => d.Id == push.Distributor)?.Name ?? push.Distributor;
        PushStatus = push.Distributor is null
            ? distributors.Count == 0 ? "The app keeps its own connection, with a notification that shows it. Install a UnifiedPush distributor such as ntfy to receive messages without it." : "The app keeps its own connection, with a notification that shows it."
            : push.Endpoint is null ? $"Waiting for {name}…" : $"Messages arrive through {name}; the app needs no connection of its own.";
    }

    /// <summary>Run <paramref name="action"/> on the user interface thread, or at once without a dispatcher.</summary>
    private void Post(Action action)
    {
        // Through the dispatcher when there is one
        if (dispatcher is null) action();
        else dispatcher.Post(action);
    }

    /// <summary>Themes to choose from, in <see cref="AppTheme"/> order.</summary>
    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];

    /// <summary>The licenses shown in place of the settings, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSettings))]
    public partial LicensesViewModel? Licenses { get; set; }

    /// <summary>The audio test shown in place of the settings, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSettings))]
    public partial AudioTestViewModel? AudioTest { get; set; }

    /// <summary>Whether the platform has audio to test.</summary>
    public bool CanTestAudio => platform?.Audio is not null;

    /// <summary>The logs shown in place of the settings, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSettings))]
    public partial LogsViewModel? Logs { get; set; }

    /// <summary>This session's log file, set once signed in; null hides the logs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowLogs))]
    public partial string? LogPath { get; set; }

    /// <summary>Whether there are logs to show.</summary>
    public bool CanShowLogs => LogPath is not null;

    /// <summary>Whether the settings are shown rather than the licenses, the logs or the audio test.</summary>
    public bool ShowsSettings => Licenses is null && AudioTest is null && Logs is null;

    /// <summary>Show the session logs, this session's first.</summary>
    [RelayCommand]
    private void ShowLogs() => Logs = LogPath is string path && Path.GetDirectoryName(path) is string directory ? new LogsViewModel(directory, path) : null;

    /// <summary>Go back from the logs to the settings.</summary>
    [RelayCommand]
    private void CloseLogs() => Logs = null;

    /// <summary>Show the audio test.</summary>
    [RelayCommand]
    private void ShowAudioTest() => AudioTest = platform?.Audio is Func<bool, IAudioDevices> audio ? new AudioTestViewModel(audio, platform.HasEarpiece, platform.RequestMicrophoneAsync) : null;

    /// <summary>Stop the audio test and go back to the settings.</summary>
    [RelayCommand]
    public void CloseAudioTest()
    {
        // Devices closed first
        AudioTest?.Dispose();
        AudioTest = null;
    }

    /// <summary>Show the licenses of the app and what it bundles.</summary>
    [RelayCommand]
    private void ShowLicenses() => Licenses = new LicensesViewModel();

    /// <summary>Go back from the licenses to the settings.</summary>
    [RelayCommand]
    private void CloseLicenses() => Licenses = null;

    /// <summary>Text sizes to choose from: small, normal and large.</summary>
    public IReadOnlyList<double> TextSizes { get; } = [13, 14, 16];

    /// <summary>Whether the platform shows notifications, so the switch is offered.</summary>
    public bool CanNotify { get; }

    /// <summary>Colour theme.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeIndex))]
    public partial AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Get or set <see cref="Theme"/> as an index of <see cref="Themes"/>, for the picker.</summary>
    public int ThemeIndex
    {
        get => (int)Theme;
        set => Theme = Enum.IsDefined((AppTheme)value) ? (AppTheme)value : AppTheme.System;
    }

    /// <summary>Base text size in pixels.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextSizeIndex))]
    public partial double TextSize { get; set; } = 14;

    /// <summary>Get or set <see cref="TextSize"/> as an index of <see cref="TextSizes"/>, for the picker.</summary>
    public int TextSizeIndex
    {
        get => TextSizes.ToList().IndexOf(TextSize) is int index and >= 0 ? index : 1;
        set => TextSize = TextSizes[Math.Clamp(value, 0, TextSizes.Count - 1)];
    }

    /// <summary>Whether the platform rings for incoming calls, so that ringing is a choice.</summary>
    public bool CanRing => platform?.Ring is not null;

    /// <summary>Whether to notify of messages in direct chats while the window is in the background; off by default, as most people do not want to be told of every message.</summary>
    [ObservableProperty]
    public partial bool NotifyDirectMessages { get; set; }

    /// <summary>Whether to notify of messages that mention the user, in any room, while the window is in the background.</summary>
    [ObservableProperty]
    public partial bool NotifyMentions { get; set; }

    /// <summary>Whether to notify of new invites to rooms and chats.</summary>
    [ObservableProperty]
    public partial bool NotifyInvites { get; set; } = true;

    /// <summary>Whether incoming calls ring and notify; off, they show only in the open app.</summary>
    [ObservableProperty]
    public partial bool NotifyCalls { get; set; } = true;

    /// <summary>Save after each change made by the user.</summary>
    partial void OnThemeChanged(AppTheme value) => Save();

    /// <summary>Save after each change made by the user.</summary>
    partial void OnTextSizeChanged(double value) => Save();

    /// <summary>Save after each change made by the user.</summary>
    partial void OnNotifyDirectMessagesChanged(bool value) => Save();

    /// <summary>Save after each change made by the user.</summary>
    partial void OnNotifyMentionsChanged(bool value) => Save();

    /// <summary>Save after each change made by the user.</summary>
    partial void OnNotifyInvitesChanged(bool value) => Save();

    /// <summary>Save after each change made by the user.</summary>
    partial void OnNotifyCallsChanged(bool value) => Save();

    /// <summary>Return the boolean <paramref name="name"/> of <paramref name="section"/>, or <paramref name="fallback"/> when it is missing or not a boolean.</summary>
    private static bool ReadBool(JsonObject section, string name, bool fallback) => section[name] is JsonValue value && value.TryGetValue(out bool flag) ? flag : fallback;

    /// <summary>Merge the values into the settings file; failures keep the values for this run.</summary>
    private void Save()
    {
        // Not while loading
        if (loading) return;
        try
        {
            JsonObject root = SettingsFile.Read(path);
            JsonObject ui = SettingsFile.Section(root, Section);
            ui[nameof(Theme)] = Theme.ToString();
            ui[nameof(TextSize)] = TextSize;
            ui.Remove("Notifications");
            ui[nameof(NotifyDirectMessages)] = NotifyDirectMessages;
            ui[nameof(NotifyMentions)] = NotifyMentions;
            ui[nameof(NotifyInvites)] = NotifyInvites;
            ui[nameof(NotifyCalls)] = NotifyCalls;
            SettingsFile.Write(path, root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Kept in memory only
        }
    }
}
