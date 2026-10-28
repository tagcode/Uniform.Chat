namespace Uniform.Matrix.Client.Avalonia.Core.ViewModels;

using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Client.Avalonia.Core.Services;
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

    /// <summary>Load the settings of <paramref name="applicationName"/>; <paramref name="canNotify"/> tells whether the platform shows notifications, and <paramref name="platform"/>, when given, offers its audio for the audio test.</summary>
    public SettingsViewModel(string applicationName, bool canNotify, GuiPlatform? platform = null)
    {
        // Missing values keep their defaults
        path = SettingsStore.DefaultPath(applicationName);
        CanNotify = canNotify;
        this.platform = platform;
        JsonObject ui = SettingsFile.Read(path)[Section] as JsonObject ?? [];
        loading = true;
        if (ui[nameof(Theme)] is JsonValue themeValue && themeValue.TryGetValue(out string? themeName) && Enum.TryParse(themeName, out AppTheme theme)) Theme = theme;
        if (ui[nameof(TextSize)] is JsonValue sizeValue && sizeValue.TryGetValue(out double size) && size is >= 10 and <= 24) TextSize = size;
        if (ui[nameof(Notifications)] is JsonValue notifyValue && notifyValue.TryGetValue(out bool notifications)) Notifications = notifications;
        loading = false;
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

    /// <summary>Whether the settings are shown rather than the licenses or the audio test.</summary>
    public bool ShowsSettings => Licenses is null && AudioTest is null;

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

    /// <summary>Whether to notify of direct messages, mentions and invites while the window is in the background.</summary>
    [ObservableProperty]
    public partial bool Notifications { get; set; } = true;

    /// <summary>Save after each change made by the user.</summary>
    partial void OnThemeChanged(AppTheme value) => Save();

    /// <summary>Save after each change made by the user.</summary>
    partial void OnTextSizeChanged(double value) => Save();

    /// <summary>Save after each change made by the user.</summary>
    partial void OnNotificationsChanged(bool value) => Save();

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
            ui[nameof(Notifications)] = Notifications;
            SettingsFile.Write(path, root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Kept in memory only
        }
    }
}
