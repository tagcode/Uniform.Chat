namespace Uniform.Chat.Avalonia.Tests;

using global::Avalonia.Headless.XUnit;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Matrix.Hosting;

/// <summary>Settings are saved on change, read back on the next start, and kept apart from the remembered account; headless so the test settings folder is in place.</summary>
public class SettingsTests
{
    /// <summary>Changed settings come back in a new instance; the account in the same file stays.</summary>
    [AvaloniaFact]
    public void SaveAndLoad()
    {
        // Arrange: a file of its own, with a remembered account
        string application = $"Uniform.Matrix.Settings.Tests.{Guid.NewGuid():N}";
        new AccountMemory(application).Save(new SignIn { UserId = "@alice:example.org" });
        SettingsViewModel settings = new(application, canNotify: true);

        // Act
        settings.ThemeIndex = 2;
        settings.TextSizeIndex = 2;
        settings.NotifyCalls = false;
        settings.NotifyDirectMessages = true;
        SettingsViewModel loaded = new(application, canNotify: true);

        // Assert
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(16, loaded.TextSize);
        Assert.False(loaded.NotifyCalls);
        Assert.True(loaded.NotifyDirectMessages);
        Assert.False(loaded.NotifyMentions);
        Assert.True(loaded.NotifyInvites);
        Assert.Equal("@alice:example.org", new AccountMemory(application).Load()?.UserId);
        Assert.Contains("\"Theme\": \"Dark\"", File.ReadAllText(SettingsStore.DefaultPath(application)));
    }

    /// <summary>Unreadable values fall back to the defaults.</summary>
    [AvaloniaFact]
    public void IgnoreBadValues()
    {
        // Arrange: wrong types in the file
        string application = $"Uniform.Matrix.Settings.Tests.{Guid.NewGuid():N}";
        string path = SettingsStore.DefaultPath(application);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Ui": { "Theme": 5, "TextSize": "huge", "NotifyCalls": "yes" } }""");

        // Act
        SettingsViewModel settings = new(application, canNotify: false);

        // Assert
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(14, settings.TextSize);
        Assert.True(settings.NotifyCalls);
        Assert.False(settings.NotifyDirectMessages);
    }

    /// <summary>The licences list the app's own license first, then the runtimes and the bundled packages, and show the chosen text; going back returns to the settings.</summary>
    [AvaloniaFact]
    public void ShowLicenses()
    {
        // Arrange
        SettingsViewModel settings = new(Path.GetRandomFileName(), canNotify: false);

        // Act
        settings.ShowLicensesCommand.Execute(null);
        LicensesViewModel licenses = settings.Licenses!;
        string own = licenses.Text;
        licenses.Selected = licenses.Entries.First(e => e.Group == "Packages" && e.Name == "Concentus");
        string concentus = licenses.Text;
        settings.CloseLicensesCommand.Execute(null);

        // Assert
        Assert.Equal("Uniform Chat", licenses.Entries[0].Name);
        Assert.Contains("MIT License", own);
        Assert.Contains(licenses.Entries, e => e.Group == "Runtime");
        Assert.Contains(licenses.Entries, e => e.Group == "Packages" && e.Name == "Avalonia");
        Assert.Contains("Redistributions in binary form must reproduce the above copyright notice", concentus);
        Assert.True(settings.ShowsSettings);
    }

    /// <summary>The logs list the sessions with the current one marked and chosen, show its entries newest first at the chosen level, and filter them by text.</summary>
    [AvaloniaFact]
    public void ShowLogs()
    {
        // Arrange: two sessions, the newer one this session's
        string directory = Directory.CreateTempSubdirectory("uniform-chat-logs-").FullName;
        string older = LogFiles.CreateSessionPath(directory, new DateTime(2026, 11, 1, 10, 0, 0));
        string current = LogFiles.CreateSessionPath(directory, new DateTime(2026, 11, 8, 12, 0, 0));
        File.WriteAllText(older, "2026-11-01 10:00:00.000 [Information] Old: before\n");
        File.WriteAllLines(current, [
            "2026-11-08 12:00:00.000 [Debug] App: details",
            "2026-11-08 12:00:01.000 [Information] App: signed in",
            "2026-11-08 12:00:02.000 [Warning] Sync: failed, retrying",
            "System.Net.Http.HttpRequestException: lost",
        ]);

        // Act
        LogsViewModel logs = new(directory, current);

        // Assert: sessions and this one's entries, newest first, with their following lines
        Assert.Equal([current, older], logs.Files.Select(f => f.File.Path));
        Assert.True(logs.Selected!.IsCurrent);
        Assert.EndsWith("(now) · 1 KiB", logs.Selected.Title);
        Assert.StartsWith("2026-11-08 12:00:02.000 [Warning] Sync: failed, retrying\nSystem.Net.Http.HttpRequestException: lost\n\n2026-11-08 12:00:01.000", logs.Text);
        Assert.DoesNotContain("details", logs.Text);

        // Act and assert: warnings only, a filter, and the older session
        logs.LevelIndex = 2;
        Assert.DoesNotContain("signed in", logs.Text);
        logs.LevelIndex = 0;
        logs.Filter = "SIGNED";
        Assert.Equal("2026-11-08 12:00:01.000 [Information] App: signed in", logs.Text);
        logs.Filter = "";
        logs.Selected = logs.Files[1];
        Assert.Contains("Old: before", logs.Text);
        Directory.Delete(directory, recursive: true);
    }
}

