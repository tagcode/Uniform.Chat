---
title: Documentation
uid: Uniform.Matrix.Documentation.Article
---
# Documentation

DocFX is a local tool pinned in `dotnet-tools.json`.

```bash
dotnet tool restore

# Developer guide and API reference
dotnet docfx docs/docfx.json
# User guides: the desktop app, the Android app, the terminal client, the server
dotnet docfx user-docs/Uniform.Chat.Desktop/docfx.json
dotnet docfx user-docs/Uniform.Chat.Android/docfx.json
dotnet docfx user-docs/Uniform.Chat.Terminal/docfx.json
dotnet docfx user-docs/Uniform.Chat.Server/docfx.json
```

`--serve` hosts the site at <http://localhost:8080>. Output goes to each site's `_site/` folder, and generated API files go to `docs/api/`; both are ignored by git. Each app has its own guide in `user-docs/`, named after its project: `Uniform.Chat.Desktop`, `Uniform.Chat.Android`, `Uniform.Chat.Terminal` and `Uniform.Chat.Server`. The Android guide has its own start and getting-started pages and includes the rest from the desktop guide, as it is the same app. Text that more than one guide needs, such as how encryption, the recovery key and verification work, is written once in `user-docs/Uniform.Matrix.Core` and included with `[!include[](../../Uniform.Matrix.Core/encryption.md)]`; it is not a site of its own.


```bash
dotnet tool restore

# Developer guide and API reference
dotnet docfx docs/docfx.json --serve

# User guides: the desktop app, the Android app, the terminal client, the server
dotnet docfx user-docs/Uniform.Chat.Desktop/docfx.json --serve
dotnet docfx user-docs/Uniform.Chat.Android/docfx.json --serve
dotnet docfx user-docs/Uniform.Chat.Terminal/docfx.json --serve
dotnet docfx user-docs/Uniform.Chat.Server/docfx.json --serve
```

All sites use DocFX's built-in `statictoc` template, which writes the table of contents into every page, so the guides also work opened from disk. `theme/` holds only what differs from it, as each `docfx.json` layers it on top (`"template": [ "statictoc", "../theme" ]` here, `"../../theme"` in the user guides): the logo, `logo_blue.svg` (the source of the logo and app icons), the favicon, `styles/main.css` with the dark look, and `styles/main.js` with the table of contents highlighting. The top of `main.css` also keeps a few styles of the older DocFX version the theme was made with; when upgrading DocFX, rebuild and look at the sites, as files of the built-in template change under it. The release archives carry them that way: each archive has its own app's guide as a `docs` folder.
