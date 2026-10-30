---
title: Building
uid: Uniform.Matrix.Building.Article
---
# Building

```bash
dotnet build
```

Every library generates its XML documentation file, so public members without doc comments show up as build warnings.

The desktop and Android apps have their own notes: [Uniform.Chat.Desktop](xref:Uniform.Chat.Desktop.Article) and [Uniform.Chat.Android](xref:Uniform.Chat.Android.Article).

## Releasing

Pushing a version tag runs `.github/workflows/release.yml`:

```bash
git tag v0.1.0
git push origin v0.1.0
```

The workflow builds and tests, packs the `Uniform.Matrix.*` libraries with the tag's version, publishes the desktop app, the terminal client and the server, each with its user guide, for `linux-x64`, `linux-arm64`, `win-x64` and `win-arm64` as self-contained single-file apps (`.tar.gz` for Linux, `.zip` for Windows), pushes the packages to NuGet.org, and creates a GitHub release with the archives and `SHA256SUMS.txt`. A tag with a suffix, such as `v0.2.0-alpha.1`, makes a prerelease on both.

Packages are pushed with [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): the job exchanges GitHub's OIDC token for a short-lived NuGet API key, so no key is stored. It needs a trusted publishing policy on nuget.org for this repository and `release.yml`, a `release` environment in the repository settings, and the nuget.org user name in the `NUGET_USER` secret.
