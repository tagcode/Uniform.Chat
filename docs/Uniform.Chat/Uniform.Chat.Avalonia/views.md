---
title: Views
uid: Uniform.Matrix.Client.Avalonia.Core.Views.Article
---
# Views

- [`ChatView`](xref:Uniform.Matrix.Client.Avalonia.Core.Views.ChatView) has the room list and the open room side by side, or one at a time below 720 pixels and always on phones. It keeps the newest message in view while scrolled to the bottom, loads older messages at the top, and goes back to the list on `TopLevel.BackRequested` (Android's back button).
- [`RichText`](xref:Uniform.Matrix.Client.Avalonia.Core.Views.RichText) is a `SelectableTextBlock` that parses a message's `formatted_body` with AngleSharp into inlines: emphasis, code, headings, quotes, lists, links and mention pills; reply fallbacks are dropped. Without HTML it shows the plain body.
- File dialogs use `TopLevel.StorageProvider`, which is the system picker on both platforms.
- The message box's keys are handled in the tunnel phase: a multi-line `TextBox` takes Enter and Ctrl+V itself, so a bubbling handler never sees them. Pasting checks the clipboard for files, then an image, and falls back to the box's own text paste.
- Links: [`RichText`](xref:Uniform.Matrix.Client.Avalonia.Core.Views.RichText) records the character range of each link while building its inlines (a `LineBreak` counts as one character) and hit-tests `TextLayout` on click, as Avalonia has no inline hyperlink. Plain-text bodies get their web addresses linked.
- Bindings are compiled (`x:DataType`), so binding errors fail the build.
