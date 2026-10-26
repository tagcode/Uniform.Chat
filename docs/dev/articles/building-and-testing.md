# Building and testing

## Build

```bash
dotnet build
```

Every library generates its XML documentation file, so public members without doc comments show up as build warnings.

## Native test oracle

The client needs no native code. Tests can compare the managed encryption with native [vodozemac](https://github.com/matrix-org/vodozemac), built from Rust into `vodozemac_ffi`:

```bash
# Once: Rust in your home directory (pinned to 1.99.0 by native/vodozemac-ffi/rust-toolchain.toml)
curl -sSf https://sh.rustup.rs | sh -s -- -y --no-modify-path --profile minimal --default-toolchain 1.99.0

# Build for this machine
native/build.sh
```

The library lands in `tests/Uniform.Matrix.Protocol.Crypto.Tests/runtimes/<rid>/native/`, which is not in git. Without it the differential tests in `OracleTests` are skipped.

## Test

The tests use xUnit v3 on Microsoft.Testing.Platform, enabled in `global.json`.

```bash
dotnet test                                         # all tests
dotnet test -- --filter-not-trait "Category=Live"   # skip tests against the real server
```

Unit tests replace the network with `StubHandler`, which records requests and returns canned responses.

`Uniform.Matrix.Protocol.Crypto.Tests` has round-trip and tamper tests of the managed Olm and Megolm (`OlmTests`), differential tests against native vodozemac (`OracleTests`: messages, out-of-order delivery, Megolm ratchet positions up to 2^32 − 1, backup encryption, SAS and pickles in both directions), and protocol tests where several `OlmMachine`s talk through `FakeKeyServer`, an in-memory stand-in for the key, cross-signing, account data, backup and to-device endpoints. `SecurityTests` cover recovery on a new device, key forwarding between own devices and SAS verification there.

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

## Documentation

DocFX is a local tool pinned in `dotnet-tools.json`.

```bash
dotnet tool restore

# Developer guide and API reference
dotnet docfx docs/dev/docfx.json --serve

# End-user guide
dotnet docfx docs/user/docfx.json --serve
```

`--serve` hosts the site at <http://localhost:8080>. Output goes to `docs/*/_site/`, and generated API files go to `docs/dev/api/`; both are ignored by git.

## Releasing

Pushing a version tag runs `.github/workflows/release.yml`:

```bash
git tag v0.1.0
git push origin v0.1.0
```

The workflow builds and tests, packs the `Uniform.Matrix.*` libraries with the tag's version, publishes the client and server for `linux-x64`, `linux-arm64`, `win-x64` and `win-arm64` as self-contained single-file apps (`.tar.gz` for Linux, `.zip` for Windows), pushes the packages to NuGet.org, and creates a GitHub release with the archives and `SHA256SUMS.txt`. A tag with a suffix, such as `v0.2.0-alpha.1`, makes a prerelease on both.

Packages are pushed with [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): the job exchanges GitHub's OIDC token for a short-lived NuGet API key, so no key is stored. It needs a trusted publishing policy on nuget.org for this repository and `release.yml`, a `release` environment in the repository settings, and the nuget.org user name in the `NUGET_USER` secret.
