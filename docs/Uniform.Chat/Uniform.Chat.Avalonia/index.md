---
title: Uniform.Matrix.Client.Avalonia.Core
uid: Uniform.Matrix.Client.Avalonia.Core.Article
---
# Uniform.Matrix.Client.Avalonia.Core

The graphical app, in Avalonia 12 with the Fluent theme and CommunityToolkit.Mvvm. The same views run in a desktop window and as the single view of the Android activity; [`GuiPlatform`](xref:Uniform.Matrix.Client.Avalonia.Core.GuiPlatform) carries what differs: the application name (and so the data folders), the device name shown to other sessions, whether the screen is always one pane, and extra services.

## Hosts

[`AccountPaths`](xref:Uniform.Matrix.Client.Configuration.AccountPaths) needs `Matrix:UserId` when the services are built, so the app has no host until someone signs in. [`ClientConnection.StartAsync`](xref:Uniform.Matrix.Client.Avalonia.Core.Services.ClientConnection.StartAsync*) builds a [`HostApplicationBuilder`](xref:Microsoft.Extensions.Hosting.HostApplicationBuilder) per sign-in: [`AddSettings`](xref:Uniform.Matrix.Hosting.HostingExtensions.AddSettings*), the user ID and server from the form as in-memory configuration, [`AddMatrixClient`](xref:Uniform.Matrix.Client.ClientServiceExtensions.AddMatrixClient*), and `GuiCredentialPrompt`, which returns the form's password or throws [`PasswordRequiredException`](xref:Uniform.Matrix.Client.Avalonia.Core.Services.PasswordRequiredException) when a resumed session needs one. Signing out or in again disposes the host and builds a new one. [`AccountMemory`](xref:Uniform.Matrix.Client.Avalonia.Core.Services.AccountMemory) keeps the last user ID in the settings file, so the next start resumes without the form. [`SettingsViewModel`](xref:Uniform.Matrix.Client.Avalonia.Core.ViewModels.SettingsViewModel) keeps the theme, text size and notification switch in its `Ui` section and saves on every change; [`App`](xref:Uniform.Matrix.Client.Avalonia.Core.App) applies the theme as `RequestedThemeVariant` and the text size as Fluent's `ControlContentThemeFontSize`.

## Not yet covered

- Renaming devices, and notifications on Windows and Android.
