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
