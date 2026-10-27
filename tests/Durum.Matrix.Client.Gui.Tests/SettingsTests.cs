namespace Durum.Matrix.Client.Gui.Tests;

using Avalonia.Headless.XUnit;
using Durum.Matrix.Client.Gui.Services;
using Durum.Matrix.Client.Gui.ViewModels;
using Uniform.Matrix.Hosting;

/// <summary>Settings are saved on change, read back on the next start, and kept apart from the remembered account; headless so the test settings folder is in place.</summary>
public class SettingsTests
{
    /// <summary>Changed settings come back in a new instance; the account in the same file stays.</summary>
    [AvaloniaFact]
    public void SaveAndLoad()
    {
        // Arrange: a file of its own, with a remembered account
        string application = $"Durum.Settings.Tests.{Guid.NewGuid():N}";
        new AccountMemory(application).Save(new SignIn { UserId = "@alice:example.org" });
        SettingsViewModel settings = new(application, canNotify: true);

        // Act
        settings.ThemeIndex = 2;
        settings.TextSizeIndex = 2;
        settings.Notifications = false;
        SettingsViewModel loaded = new(application, canNotify: true);

        // Assert
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(16, loaded.TextSize);
        Assert.False(loaded.Notifications);
        Assert.Equal("@alice:example.org", new AccountMemory(application).Load()?.UserId);
        Assert.Contains("\"Theme\": \"Dark\"", File.ReadAllText(SettingsStore.DefaultPath(application)));
    }

    /// <summary>Unreadable values fall back to the defaults.</summary>
    [AvaloniaFact]
    public void IgnoreBadValues()
    {
        // Arrange: wrong types in the file
        string application = $"Durum.Settings.Tests.{Guid.NewGuid():N}";
        string path = SettingsStore.DefaultPath(application);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Ui": { "Theme": 5, "TextSize": "huge", "Notifications": "yes" } }""");

        // Act
        SettingsViewModel settings = new(application, canNotify: false);

        // Assert
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(14, settings.TextSize);
        Assert.True(settings.Notifications);
    }
}
