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

The workflow builds and tests, packs the `Uniform.Matrix.*` libraries with the tag's version, publishes the desktop app, the terminal client and the server, each with its user guide, for `linux-x64`, `linux-arm64`, `win-x64` and `win-arm64` as self-contained single-file apps (`.tar.gz` for Linux, `.zip` for Windows), pushes the packages to NuGet.org, and creates a GitHub release with the archives, the signed Android APK `uniform-chat-android-<version>.apk` and `SHA256SUMS.txt`. A tag with a suffix, such as `v0.2.0-alpha.1`, makes a prerelease on both.

Packages are pushed with [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): the job exchanges GitHub's OIDC token for a short-lived NuGet API key, so no key is stored. It needs a trusted publishing policy on nuget.org for this repository and `release.yml`, a `release` environment in the repository settings, and the nuget.org user name in the `NUGET_USER` secret.

### Android release signing

The APK is signed with the app's release key, which Android uses to allow updates: an update signed with another key does not install over the app. Keep the keystore and its passwords safe and backed up; a lost key means users have to uninstall to get updates.

Create the keystore once, outside the repository; keytool asks for the passwords:

```bash
keytool -genkeypair -keystore ~/uniform-chat-release.keystore -alias uniform-chat -keyalg RSA -keysize 4096 -validity 10000 -dname "CN=Uniform Chat"
```

Then add these repository secrets; the workflow writes the keystore to the runner's temporary folder and deletes it after the build. Without `ANDROID_KEYSTORE_BASE64`, the release has no APK.

| Secret | Value |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | The keystore, from `base64 -w0 ~/uniform-chat-release.keystore` |
| `ANDROID_KEY_ALIAS` | The alias, e.g. `uniform-chat` |
| `ANDROID_KEYSTORE_PASSWORD` | The keystore password |
| `ANDROID_KEY_PASSWORD` | The key password |

To build a signed APK locally, give the keystore and alias as properties and the passwords in the environment, never on the command line:

```bash
read -rs ANDROID_KEYSTORE_PASSWORD && export ANDROID_KEYSTORE_PASSWORD ANDROID_KEY_PASSWORD="$ANDROID_KEYSTORE_PASSWORD"
dotnet publish src/Uniform.Chat.Android -c Release -p:Version=0.1.0 \
  -p:AndroidSigningKeyStore=$HOME/uniform-chat-release.keystore -p:AndroidSigningKeyAlias=uniform-chat
```

The version code follows the version: major × 10000 + minor × 100 + patch, so `0.2.1` is 201. It has to grow with every release that app stores get.

### Store listings

`fastlane/metadata/android/en-US/` holds the title, the short and full description, the icon, screenshots and a changelog per version code, e.g. `changelogs/201.txt`, in the layout F-Droid and IzzyOnDroid read. Add the changelog of a version before tagging it.

