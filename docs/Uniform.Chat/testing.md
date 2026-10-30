---
title: Testing
uid: Uniform.Matrix.Testing.Article
---
# Testing

The tests use xUnit v3 on Microsoft.Testing.Platform, enabled in `global.json`.

```bash
dotnet test                                         # all tests
dotnet test -- --filter-not-trait "Category=Live"   # skip tests against the real server
```

Unit tests replace the network with `StubHandler`, which records requests and returns canned responses.

`Uniform.Matrix.Protocol.Crypto.Tests` has round-trip and tamper tests of the managed Olm and Megolm (`OlmTests`), differential tests against native vodozemac (`OracleTests`: messages, out-of-order delivery, Megolm ratchet positions up to 2^32 − 1, backup encryption, SAS and pickles in both directions), and protocol tests where several [`OlmMachine`](xref:Uniform.Matrix.Protocol.Crypto.OlmMachine)s talk through `FakeKeyServer`, an in-memory stand-in for the key, cross-signing, account data, backup and to-device endpoints. `SecurityTests` cover recovery on a new device, key forwarding between own devices and SAS verification there.

Live tests (`Category=Live`) log in to a real homeserver with the accounts in `testclient.txt` at the repository root, which share one password:

```text
@localpart:server
@localpart2:server
@localpart3:server
password: ...
```

Both files are in `.gitignore`, as they hold a password and recovery keys; without `testclient.txt` the live tests are skipped. `Uniform.Matrix.Server.Tests` run the same encryption flows against the in-memory server, with no accounts needed.

Most live tests use the first account. `LiveSecurityTests` use the second and third: the first run sets up cross-signing, key backup and a recovery key on them and saves the recovery keys in `testclient-recovery.txt` (user ID, tab, key); later runs recover with those keys.

Every live test logs out at the end, so no stale devices are left on the account. Verification requests name the test device, so the account's other sessions are not asked.

`Uniform.Matrix.Client.Avalonia.Core.Tests` drive the app's view models against the in-memory server, with Avalonia's headless platform and Skia rendering, so they need no display. Set `UNIFORM_MATRIX_SCREENSHOTS` to a folder to save the rendered chat window there. They pin xUnit v3 3.2, as `Avalonia.Headless.XUnit` 12 does not run on xUnit 4.

## Native test oracle

The client needs no native code. Tests can compare the managed encryption with native [vodozemac](https://github.com/matrix-org/vodozemac), built from Rust into `vodozemac_ffi`:

```bash
# Once: Rust in your home directory (pinned to 1.99.0 by native/vodozemac-ffi/rust-toolchain.toml)
curl -sSf https://sh.rustup.rs | sh -s -- -y --no-modify-path --profile minimal --default-toolchain 1.99.0

# Build for this machine
native/build.sh
```

The library lands in `tests/Uniform.Matrix.Protocol.Crypto.Tests/runtimes/<rid>/native/`, which is not in git. Without it the differential tests in `OracleTests` are skipped.

The server's own tests, with two servers federating in memory, are described in [Uniform.Matrix.Server](xref:Uniform.Matrix.Server.Testing.Article).
