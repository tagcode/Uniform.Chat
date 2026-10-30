---
title: Uniform.Matrix.Hosting
uid: Uniform.Matrix.Hosting.Article
---
# Uniform.Matrix.Hosting

Settings and logging for any application on the .NET generic host, the client's and later the server's. Registered with `builder.AddSettings()`, `builder.AddSessionLogging(directory, keep)` and `services.AddOptionSection<T>(configuration, section)`; folders follow the XDG base directories through [`AppDirectories`](xref:Uniform.Matrix.Hosting.AppDirectories).

## Settings

[`SettingsStore`](xref:Uniform.Matrix.Hosting.SettingsStore) adds two configuration layers: the application's settings file (`$XDG_CONFIG_HOME/<app>/settings.json`, where `<app>` is the host's application name in kebab case, e.g. `uniform-matrix-client-terminal`) right after `appsettings.json`, so environment and command line still win, and an in-memory *applied* layer on top. Applying sets the top layer and reloads the configuration; saving merges into the file, clears those keys from the applied layer and reloads. Options read through `IOptionsMonitor<UiOptions>` change at once; the others are marked `[RestartRequired]` and read at start. Each application registers its option records with `AddOptionSection<T>`, and [`OptionCatalog`](xref:Uniform.Matrix.Hosting.OptionCatalog) lists their properties by reflection, with descriptions read from the XML documentation of each record's assembly, so the settings window and the API reference say the same. `[Secret]` options are not shown, saved or exported. [`SettingsEditor`](xref:Uniform.Matrix.Hosting.SettingsEditor) holds the window's edits and is tested without the UI.

## Logs

[`FileLoggerProvider`](xref:Uniform.Matrix.Hosting.FileLoggerProvider) writes `logs/client-yyyyMMdd-HHmmss.log` per session and prunes the oldest beyond `Storage:LogFilesKept`. [`LogFiles`](xref:Uniform.Matrix.Hosting.LogFiles) lists and parses them: a line with time and level starts an entry, other lines (stack traces) continue it.
