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

The workflow builds and tests, packs the `Uniform.Matrix.*`, `Uniform.Stun.*` and `Uniform.Turn` libraries with the tag's version, publishes the desktop app, the terminal client, the server and the STUN and TURN server, each with its user guide, for `linux-x64`, `linux-arm64`, `win-x64` and `win-arm64` as self-contained single-file apps (`.tar.gz` for Linux, `.zip` for Windows), pushes the packages to NuGet.org, and creates a GitHub release with the archives, the signed Android APK `uniform-chat-android-<version>.apk` and `SHA256SUMS.txt`. A tag with a suffix, such as `v0.2.0-alpha.1`, makes a prerelease on both.

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

### Documentation on GitHub Pages

After each release, but not a prerelease, the workflow publishes the documentation of that release on GitHub Pages, from the [tagcode.github.io](https://github.com/tagcode/tagcode.github.io) repository, in the same folders as here:

| Address | Site |
|---|---|
| `https://tagcode.github.io/Uniform.Chat/docs/` | This developer site, with the API reference |
| `https://tagcode.github.io/Uniform.Chat/user-docs/Uniform.Chat.Desktop/` | The user guide of the desktop app; likewise `Uniform.Chat.Android`, `Uniform.Chat.Terminal`, `Uniform.Chat.Server` and `Uniform.Stun.Server` |

Each release replaces the previous documentation. The same job updates the F-Droid repository below, so both go into one commit. It needs the `PAGES_TOKEN` secret, described below; without the F-Droid secrets only the documentation is published.

### F-Droid repository

After each release, but not a prerelease, the workflow adds the APK to the app's own F-Droid repository, which GitHub Pages serves from the [tagcode.github.io](https://github.com/tagcode/tagcode.github.io) repository at `https://tagcode.github.io/fdroid/repo`. [fdroidserver](https://f-droid.org/docs/Setup_an_F-Droid_App_Repo/) reads the APKs, the store listing from `fastlane/metadata/android/` and the app's entry from `fdroid/metadata/rip.uniform.chat.yml`, and signs the index with the repository key. The repository keeps only the newest version, as the site's space is limited; users can't go back to an older one through F-Droid. Prereleases are left out because they share the version code of their release.

The repository key is separate from the app's release key. F-Droid users trust the repository by its fingerprint, so a lost key means they have to add the repository again. Create it once, outside the repository:

```bash
keytool -genkeypair -keystore ~/uniform-chat-fdroid.keystore -alias fdroid-repo -keyalg RSA -keysize 4096 -sigalg SHA256withRSA -validity 10000 -dname "CN=Uniform Chat F-Droid repository"
```

Then add these repository secrets. Without them, the release is not added to the F-Droid repository.

| Secret | Value |
|---|---|
| `FDROID_KEYSTORE_BASE64` | The keystore, from `base64 -w0 ~/uniform-chat-fdroid.keystore` |
| `FDROID_KEY_ALIAS` | The alias, e.g. `fdroid-repo` |
| `FDROID_KEYSTORE_PASSWORD` | The keystore password, which the key shares |
| `PAGES_TOKEN` | A fine-grained token for `tagcode/tagcode.github.io` only, with *Contents: Read and write* |

Each update replaces the Pages repository's history with one commit, so the APKs of dropped versions and old documentation don't make it grow. The site's other files stay as they are. To be offered the repository, users open `https://tagcode.github.io/fdroid/repo` and scan or tap its link, which carries the key's fingerprint.

