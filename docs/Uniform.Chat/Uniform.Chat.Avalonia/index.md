---
title: Uniform.Chat.Avalonia
uid: Uniform.Chat.Avalonia.Article
---
# Uniform.Chat.Avalonia

The graphical app, in Avalonia 12 with the Fluent theme and CommunityToolkit.Mvvm. The same views run in a desktop window and as the single view of the Android activity; [`GuiPlatform`](xref:Uniform.Chat.Avalonia.GuiPlatform) carries what differs: the application name (and so the data folders), the device name shown to other sessions, whether the screen is always one pane, and extra services.

## Hosts

[`AccountPaths`](xref:Uniform.Matrix.Client.Configuration.AccountPaths) needs `Matrix:UserId` when the services are built, so the app has no host until someone signs in. [`ClientConnection.StartAsync`](xref:Uniform.Chat.Avalonia.Services.ClientConnection.StartAsync*) builds a [`HostApplicationBuilder`](xref:Microsoft.Extensions.Hosting.HostApplicationBuilder) per sign-in: [`AddSettings`](xref:Uniform.Matrix.Hosting.HostingExtensions.AddSettings*), the user ID and server from the form as in-memory configuration, [`AddMatrixClient`](xref:Uniform.Matrix.Client.ClientServiceExtensions.AddMatrixClient*), and `GuiCredentialPrompt`, which returns the form's password or throws [`PasswordRequiredException`](xref:Uniform.Chat.Avalonia.Services.PasswordRequiredException) when a resumed session needs one. Signing out or in again disposes the host and builds a new one. [`AccountMemory`](xref:Uniform.Chat.Avalonia.Services.AccountMemory) keeps the last user ID in the settings file, so the next start resumes without the form. [`SettingsViewModel`](xref:Uniform.Chat.Avalonia.ViewModels.SettingsViewModel) keeps the theme, text size and notification switch in its `Ui` section and saves on every change; [`App`](xref:Uniform.Chat.Avalonia.App) applies the theme as `RequestedThemeVariant` and the text size as Fluent's `ControlContentThemeFontSize`.

## Not yet covered

- Renaming devices, and notifications on Windows and Android.
