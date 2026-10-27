# Third-party notices

## vodozemac

The Olm and Megolm implementation in `Uniform.Matrix.Protocol.Crypto` is a C# port of [vodozemac](https://github.com/matrix-org/vodozemac) 0.11.1, the Rust implementation of Olm and Megolm by the Matrix.org Foundation.

- Copyright 2021 The Matrix.org Foundation C.I.C.
- Copyright 2021 Damir Jelić, Denis Kasak
- Copyright OpenMarket Ltd

Licensed under the Apache License, Version 2.0; see [LICENSES/Apache-2.0.txt](LICENSES/Apache-2.0.txt) or <http://www.apache.org/licenses/LICENSE-2.0>.

Ported files: `OlmAccount.cs`, `OlmSession.cs`, `Megolm.cs`, `OlmKeys.cs`, `OlmCipher.cs` and `OlmPrimitives.cs`.

Changes: the code was translated from Rust to C#, and restructured to fit .NET. Curve25519 and Ed25519 come from BouncyCastle instead of the Rust crates. The message, session key and pickle formats are kept, so data moves between the two implementations; tests check this against vodozemac itself.

The native wrapper in `native/vodozemac-ffi`, used only by those tests, links vodozemac unchanged.

## Bundled in the apps

The release archives of the apps are self-contained: they include the .NET runtime and these libraries. Each keeps its own license; the libraries' license texts are at the linked projects.

| Library | Used by | License |
|---|---|---|
| [.NET runtime and Microsoft.Extensions](https://github.com/dotnet/runtime), [Entity Framework Core](https://github.com/dotnet/efcore) | all apps | MIT |
| [SQLite](https://sqlite.org) via [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | all apps | public domain; Apache-2.0 |
| [BouncyCastle](https://github.com/bcgit/bc-csharp) | all apps | MIT |
| [Markdig](https://github.com/xoofx/markdig) | clients | BSD-2-Clause |
| [AngleSharp](https://github.com/AngleSharp/AngleSharp) | clients | MIT |
| [StbImageSharp, StbImageWriteSharp](https://github.com/StbSharp) | clients | public domain |
| [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) | terminal client, server dashboard | MIT |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | desktop and Android clients | MIT |
| [SkiaSharp and HarfBuzzSharp](https://github.com/mono/SkiaSharp) | desktop and Android clients | MIT; Skia BSD-3-Clause, HarfBuzz Old MIT |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | desktop client on Linux | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | desktop and Android clients | MIT |
| [Inter font](https://github.com/rsms/inter) | desktop and Android clients | SIL Open Font License 1.1; see [LICENSES/OFL-1.1.txt](LICENSES/OFL-1.1.txt) |
