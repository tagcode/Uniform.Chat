---
title: Uniform.Chat.Terminal
uid: Uniform.Chat.Terminal.Article
---
# Uniform.Chat.Terminal

The terminal app: [`Program`](xref:Uniform.Chat.Desktop.Program) calls [`AddSettings`](xref:Uniform.Matrix.Hosting.HostingExtensions.AddSettings*), [`AddMatrixClient`](xref:Uniform.Matrix.Client.ClientServiceExtensions.AddMatrixClient*) and registers its [`UiOptions`](xref:Uniform.Chat.Terminal.Configuration.UiOptions), then runs the UI.

The program file is `Uniform.Chat` (`Uniform.Chat.exe` on Windows), like the desktop app's. `Program` sets the host's application name to `Uniform.Chat.Terminal`, because the settings, data and log folders are named after it, not after the program file.

## User interface

[`Program`](xref:Uniform.Chat.Desktop.Program) creates the Terminal.Gui application first, starts the host's background services and then runs [`MainWindow`](xref:Uniform.Chat.Terminal.Ui.MainWindow) on the main thread until the user quits. Background work never touches views directly: sync changes, outbox changes and connection status are marshalled with `IApplication.Invoke`, and the password prompt shows its dialog the same way.

- [`MainWindow`](xref:Uniform.Chat.Terminal.Ui.MainWindow) lays out the menu bar, room list, [`TimelinePane`](xref:Uniform.Chat.Terminal.Ui.TimelinePane), member list, input line and status bar, and handles menu actions, `/commands` and keys. Shortcuts are handled application-wide in one key handler; menu items only show them.
- [`TimelinePane`](xref:Uniform.Chat.Terminal.Ui.TimelinePane) is a `ListView` of wrapped lines. It rewraps on resize, colors lines by [`EntryKind`](xref:Uniform.Chat.Terminal.Ui.EntryKind), inserts day separators, keeps pending outbox messages last and follows new lines while scrolled to the bottom.
- `MainWindow.Messages.cs` holds message actions (reply, react, edit, delete, save), compose modes, typing notifications, nick completion, file sending and the devices dialog. Replies, edits and reactions go through the outbox like messages; uploads and redactions are sent directly.
- `MainWindow.Images.cs` shows image previews. An entry with an `ImageUri` asks [`TimelinePane.PreviewProvider`](xref:Uniform.Chat.Terminal.Ui.TimelinePane.PreviewProvider) for lines on every rebuild; the first request starts a background load of an 800×600 thumbnail through [`MediaCache`](xref:Uniform.Matrix.Client.Services.MediaCache) (falling back to the full image), decodes it with StbImageSharp into [`DecodedImage`](xref:Uniform.Chat.Terminal.Ui.Images.DecodedImage), then refreshes the pane. [`AsciiArt.Render`](xref:Uniform.Chat.Terminal.Ui.Images.AsciiArt.Render*) samples a brightness grid (one value per cell, or 2×4 per cell for Braille), stretches its contrast, then maps it to shade blocks ` ░▒▓█` with Floyd–Steinberg dithering between the five levels, to the 70-step ASCII ramp, or to dithered Braille dots. It corrects for cells being about twice as tall as wide. Previews fill the pane unless limited in settings and are cached per image and size; the pane rebuilds on height changes while it shows images. The viewer feeds a downscaled image to Terminal.Gui's `ImageView`, which uses Kitty or Sixel graphics when available.
- [`MessageFormatter`](xref:Uniform.Chat.Terminal.Ui.MessageFormatter) and [`TextWrapper`](xref:Uniform.Matrix.Gui.Terminal.TextWrapper) are pure functions and unit-tested. Wrapping counts terminal columns, so emoji and CJK take two.
- A dialog opened from another dialog's result is queued with `IApplication.Invoke`; opening it directly lets the key that closed the first dialog reach the second.
- Terminal.Gui 2.5 does not report F3 when the terminal sends `ESC O R` (most xterm-style terminals do), so message selection is on Ctrl+Up.
- Reading [`Room`](xref:Uniform.Matrix.Client.State.Room) state for display goes through [`SyncProcessor.Read`](xref:Uniform.Matrix.Client.Services.SyncProcessor.Read*), as [`RoomStore`](xref:Uniform.Matrix.Client.State.RoomStore) is not thread-safe. History and member loading fetch from the server outside the lock and apply inside it.
