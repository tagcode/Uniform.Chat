# Uniform.Matrix.Hosting

Settings and logging for applications on the .NET generic host: a user settings file between `appsettings.json` and the command line, a catalog of every option with its description, an editor that applies or saves changes, and one log file per session.

```bash
dotnet add package Uniform.Matrix.Hosting
```

## Set up

```csharp
using Microsoft.Extensions.Hosting;
using Uniform.Matrix.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddSettings();
builder.Services.AddOptionSection<PlayerOptions>(builder.Configuration, PlayerOptions.Section);
builder.AddSessionLogging(Path.Combine(AppDirectories.Data(builder.Environment.ApplicationName), "logs"), keep: 20);
```

Options are plain records; their XML doc comments become the descriptions in the catalog.

```csharp
/// <summary>Playback settings, configuration section <see cref="Section"/>.</summary>
public sealed record PlayerOptions
{
    public const string Section = "Player";

    /// <summary>Volume from 0 to 10.</summary>
    public int Volume { get; set; } = 5;
    /// <summary>Output device; read at start only.</summary>
    [RestartRequired]
    public string? Device { get; set; }
    /// <summary>Account password; never shown or saved.</summary>
    [Secret]
    public string? Password { get; set; }
}
```

## What you get

- Configuration layers, lowest first: `appsettings.json`, the settings file `~/.config/<app>/settings.json` (under `$XDG_CONFIG_HOME`), environment variables, the command line, then changes applied at run time. Options read through `IOptionsMonitor` see applied changes.
- `OptionCatalog` lists every registered option with its type, default, current value, source and description; `SettingsEditor` edits them and applies, saves, resets, loads and exports.
- `AppDirectories` gives XDG data and config folders named after the application, e.g. `~/.local/share/my-app`.
- Session logs keep the newest files and remove older ones at start; `FileLoggerProvider.FilePath` is the current one.

For a terminal UI over these, see [Uniform.Matrix.Gui.Terminal](https://www.nuget.org/packages/Uniform.Matrix.Gui.Terminal).

## About

Part of [Uniform.Matrix](https://github.com/tagcode/Uniform.Matrix), a pure .NET implementation of the [Matrix specification](https://spec.matrix.org/latest/). Version 0.1 is an early pre-release: APIs may still change.

- [How it works](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/Uniform.Matrix.Hosting/index.md)
- [Building](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/building.md) and [testing](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/testing.md)
