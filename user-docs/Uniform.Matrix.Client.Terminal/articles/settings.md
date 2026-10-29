---
title: Settings
uid: Uniform.Matrix.Client.Terminal.Settings.Article
---
# Settings

Settings come from these places; later ones override earlier ones:

1. `appsettings.json` next to the program
2. `~/.config/uniform-matrix-client-terminal/settings.json` (under `$XDG_CONFIG_HOME` when set), written by the settings window
3. Environment variables, with `__` between parts: `Matrix__UserId=@alice:matrix.org`
4. The command line: `--Matrix:UserId=@alice:matrix.org`
5. Changes applied in the settings window, until the client exits

## Application parameters

Every setting can be given on the command line as `--Section:Name=value`. The ones you need at the start:

```bash
./Uniform.Matrix --Matrix:UserId=@alice:matrix.org --Matrix:Password=secret
```

- `Matrix:UserId` is your full Matrix ID, such as `@alice:matrix.org`. It is needed at every start, as it chooses your local data; save it in the settings file to start without options. Without one, the client asks for it in the window.
- `Matrix:Password` is used only for the first login. When it is not given, the client asks for it, which keeps it out of your shell history.
- `Matrix:Homeserver` is the server address, for servers that cannot be found from the user ID.

`--help` lists every parameter with its default. All of them:

| Parameter | Default | Meaning |
|---|---|---|
| `Matrix:UserId` | not set | Your full Matrix ID, e.g. @alice:matrix.org. Needed at every start; save it in the settings file. |
| `Matrix:Password` | not set | Password for the first login; asked for when not given. Not needed once you are logged in. |
| `Matrix:Homeserver` | not set | Server address, when it cannot be found from the user ID, e.g. http://localhost:8008 for a server on this computer. |
| `Matrix:DeviceName` | `Uniform.Matrix.Client.Terminal` | Display name of the device created at login. |
| `Matrix:SyncTimeout` | `00:00:30` | Long-poll time of each sync request. |
| `Matrix:RequestTimeout` | `00:05:00` | How long to wait for the server to answer a request; joining a large room on another server can take minutes. |
| `Matrix:TimelineLimit` | `50` | Timeline events per room in each sync. |
| `Matrix:OutboxRetryInterval` | `00:00:15` | Wait before retrying unsent messages after a transient failure. |
| `Storage:DataDirectory` | not set | Root directory; when unset, the application's data directory, e.g. ~/.local/share/uniform-matrix-client-terminal. |
| `Storage:DatabaseFileName` | `client.db` | Database file name inside each account directory. |
| `Storage:MediaDirectoryName` | `media` | Media cache directory name inside each account directory. |
| `Storage:LogDirectoryName` | `logs` | Directory inside DataDirectory with one log file per session. |
| `Storage:LogFilesKept` | `20` | Session log files to keep; older ones are deleted at start. |
| `Ui:StartRoom` | not set | Room ID or alias to open at start; the first room in the list when null. |
| `Ui:InlineImages` | `true` | Whether to show ASCII previews of images in the message log. |
| `Ui:ImagePreviewColumns` | `0` | Widest preview in terminal columns; 0 fills the width of the message log. |
| `Ui:ImagePreviewRows` | `0` | Tallest preview in terminal rows; 0 allows three quarters of the message log's height. |
| `Ui:ImagePreviewStyle` | `Blocks` | Drawing style: Blocks shades, Ascii characters or Braille dots. |
| `Ui:DarkBackground` | `true` | Whether the terminal background is dark, so bright pixels get dense characters. |
| `Ui:DownloadDirectory` | not set | Folder that saved files go to; when null, ~/Downloads if it exists, otherwise the home folder. |
| `Ui:AskWhereToSave` | `true` | Whether Save file asks where to save, starting from DownloadDirectory; otherwise files are saved there under a free name. |
| `Audio:Microphone` | not set | Microphone: an ALSA device name on Linux, e.g. pulse, or part of the device's name on Windows; the system's default when null. The audio test lists the choices. |
| `Audio:Speaker` | not set | Speaker: an ALSA device name on Linux, or part of the device's name on Windows; the system's default when null. The audio test lists the choices. |
| `Logging:LogLevel:Default` | `Information` | Lowest level written to the log for categories without their own setting. |

## Settings window

File → Settings (or `/settings`) lists every option with its current value, where the value comes from, and a description of the selected option.

- **Edit** (or Enter) changes the selected option; options with fixed values, such as yes/no or the image preview style, offer a list.
- **Apply** uses the changes now, until the client exits. Options marked ↻ take effect only after a restart.
- **Save** writes the changes to `~/.config/uniform-matrix-client-terminal/settings.json` (under `$XDG_CONFIG_HOME` when set), which is read at every start.
- **Reset** undoes one step at a time: first an edit that is not applied, then an applied change that is not saved. Pressed again, it removes the option from the settings file on save, so the default applies again.
- **Load** reads another settings file as changes to apply or save; **Export** writes the current settings to a file.

Edited options are marked `*`, applied but unsaved ones `+`. Values given on the command line override the settings file; the source column shows `command line` for them. The password is never shown or saved.

## Logs

View → Logs (or `/logs`) shows the log of this run and earlier ones; each run writes its own file, and the oldest beyond `Storage:LogFilesKept` are deleted. Pick a run on the left. **Level** cycles the lowest level shown, Alt+F focuses the text filter, and Enter shows a whole entry, such as an error with its stack trace. Warnings are yellow and errors red; the current run's log follows new lines while scrolled to the end.
