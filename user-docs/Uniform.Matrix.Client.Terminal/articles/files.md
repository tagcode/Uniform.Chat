# Files and images

## Files

Room → Send file (or `/upload`) opens a file browser; the file is uploaded and sent as an image, video, audio or file message. You can also drag files onto the terminal window, or paste their paths: when the input line holds nothing but paths of existing files, Enter asks whether to send them, and No puts the text back to edit.

Attachments show as `[image: cat.png, 120 KB]`. The Save file action asks where to save, starting with a free name in your downloads folder; give a file, or a folder to save into. The folder is `Ui:DownloadDirectory`, or `~/Downloads` when it is unset; with `Ui:AskWhereToSave` off, files are saved there without asking.

## Images

Images in the log get a preview under the message, drawn with shade blocks:

```text
20:01 <alice> [image: ring.png, 1 KB]
      ░▒▓▓█████▓▓▒░
    ░▓██▓▒░    ░▒▓██▓░
    ▓██░          ░██▓
    ▓██░          ░██▓
    ░▓██▓▒░    ░▒▓██▓░
      ░▒▓▓█████▓▓▒░
```

The preview grows with the window: by default it uses the full width of the message log and up to three quarters of its height, and it is redrawn when you resize the terminal. It is made from an 800×600 thumbnail, so it loads quickly.

The View image action opens the full image: terminals that support the Kitty or Sixel graphics protocols (kitty, WezTerm, foot, Konsole, recent xterm) show real pixels; others show colored blocks. PNG, JPEG, GIF, BMP and TGA are supported; other formats such as WebP show `[no preview]` and can still be saved.

Previews are set in `appsettings.json`:

```json
"Ui": {
  "InlineImages": true,
  "ImagePreviewColumns": 0,
  "ImagePreviewRows": 0,
  "ImagePreviewStyle": "Blocks",
  "DarkBackground": true
}
```

| Setting | Meaning |
|---|---|
| `StartRoom` | Room ID or alias to open at start; the first room when unset |
| `InlineImages` | `false` turns previews off |
| `ImagePreviewColumns`, `ImagePreviewRows` | Largest preview in cells; `0` follows the window |
| `ImagePreviewStyle` | `Blocks` (` ░▒▓█`, dithered), `Ascii` (70 characters from ` .` to `@$`) or `Braille` (2×4 dots per cell, the most detail) |
| `DarkBackground` | `false` on a light terminal, so dark parts of the image are drawn |
