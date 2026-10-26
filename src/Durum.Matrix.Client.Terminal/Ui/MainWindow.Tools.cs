namespace Durum.Matrix.Client.Terminal.Ui;

using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Hosting;

// Settings and log windows.
public sealed partial class MainWindow
{
    /// <summary>Open the settings window; ask before closing with unsaved changes.</summary>
    private void ShowSettings()
    {
        // Run until closed with nothing unsaved, or confirmed
        SettingsEditor editor = new(settings, optionCatalog.GetAll(configuration));
        using SettingsWindow window = new(app, editor);
        do app.Run(window);
        while (!window.ConfirmClose());
    }

    /// <summary>Open the log window on this session's log.</summary>
    private void ShowLogs()
    {
        // Session logs, and the single log of older versions
        string dataDirectory = storageOptions.ResolveDataDirectory(environment.ApplicationName);
        using LogWindow window = new(app, storageOptions.ResolveLogDirectory(environment.ApplicationName), fileLogger.FilePath, Path.Combine(dataDirectory, "client.log"));
        app.Run(window);
    }

    /// <summary>Redraw with new display settings, e.g. another preview style.</summary>
    private void OnUiOptionsChanged()
    {
        // Previews are rendered per setting
        renderedPreviews.Clear();
        timeline.Refresh();
    }
}
