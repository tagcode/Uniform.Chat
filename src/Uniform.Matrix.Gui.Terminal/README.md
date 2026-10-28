# Uniform.Matrix.Gui.Terminal

[Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) v2 components for host applications that use [Uniform.Matrix.Hosting](https://www.nuget.org/packages/Uniform.Matrix.Hosting): a settings window, a log window and common dialogs.

```bash
dotnet add package Uniform.Matrix.Gui.Terminal
```

## Settings window

Lists every option with its value, source and description, and edits, applies, saves, resets, loads and exports them. Options marked `[RestartRequired]` are flagged, and `[Secret]` ones are never shown.

```csharp
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Hosting;

SettingsStore settings = services.GetRequiredService<SettingsStore>();
OptionCatalog catalog = services.GetRequiredService<OptionCatalog>();
IConfiguration configuration = services.GetRequiredService<IConfiguration>();

using SettingsWindow window = new(app, new SettingsEditor(settings, catalog.GetAll(configuration)));
do app.Run(window);
while (!window.ConfirmClose());
```

## Log window

Shows this session's log and earlier ones, with levels in colour.

```csharp
FileLoggerProvider logger = services.GetRequiredService<FileLoggerProvider>();
using LogWindow window = new(app, logDirectory, logger.FilePath, legacyPath: null);
app.Run(window);
```

## Dialogs

`Dialogs` has one-call dialogs that run on the given `IApplication`: `Ask` (optionally masked, for passwords), `AskAllowEmpty`, `PickOne`, `Confirm`, `Choose`, `Info`, `Error`, `ShowText`, `ShowCopyable` and `PickFile`.

## About

Part of [Uniform.Matrix](https://github.com/tagcode/Uniform.Matrix), a pure .NET implementation of the [Matrix specification](https://spec.matrix.org/latest/). Version 0.1 is an early pre-release: APIs may still change.

- [How it works](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/Uniform.Matrix.Gui.Terminal/index.md)
- [Building](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/building.md) and [testing](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/testing.md)
