namespace Uniform.Matrix.Client.Avalonia.Core.Tests;

using global::Avalonia.Headless.XUnit;
using Uniform.Matrix.Client.Avalonia.Core.Services;
using Uniform.Matrix.Client.Avalonia.Core.ViewModels;
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
        string application = $"Uniform.Matrix.Settings.Tests.{Guid.NewGuid():N}";
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
        Assert.Equal("Uniform.Matrix", licenses.Entries[0].Name);
        Assert.Contains("MIT License", own);
        Assert.Contains(licenses.Entries, e => e.Group == "Runtime");
        Assert.Contains(licenses.Entries, e => e.Group == "Packages" && e.Name == "Avalonia");
        Assert.Contains("Redistributions in binary form must reproduce the above copyright notice", concentus);
        Assert.True(settings.ShowsSettings);
    }
}

