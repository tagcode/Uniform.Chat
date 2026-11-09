#!/bin/bash
# Make the apt repository index of a release, to upload as its assets: Packages lists this release's .deb files and those of the
# earlier releases, Release has the hashes, InRelease and Release.gpg sign it, and uniform-chat-archive-keyring.gpg is the public key.
# apt reads a flat repository at https://github.com/<repository>/releases/ latest/download/, which GitHub serves from the newest release,
# and fetches each package from download/<tag>/<file> there, so every release in the index can be installed.
# Usage: build-apt-index.sh <folder with .deb files> <tag> <URL of the previous Packages> <output folder>
# The signing key is the only secret key in GnuPG's keyring ($GNUPGHOME); its passphrase is in APT_GPG_PASSPHRASE.
set -euo pipefail
debs=$1 tag=$2 previous=$3 out=$4
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# This release's packages, listed with the path apt fetches them from
mkdir -p "$work/download/$tag"
cp "$debs"/*.deb "$work/download/$tag/"
(cd "$work" && apt-ftparchive packages download) > "$work/new"

# The earlier releases' packages from the previous index, none for the first; without those this release builds again
if ! curl -fsSL "$previous" -o "$work/old"; then
    echo "No previous index at $previous; starting a new one"
    : > "$work/old"
fi
awk '
    function field(stanza, name,   line) {
        if (!match(stanza, "(^|\n)" name ": [^\n]*")) return ""
        line = substr(stanza, RSTART, RLENGTH)
        sub(/^\n/, "", line)
        sub(/^[^:]*: /, "", line)
        return line
    }
    function key(stanza) { return field(stanza, "Package") "|" field(stanza, "Version") "|" field(stanza, "Architecture") }
    BEGIN { RS = ""; ORS = "\n\n" }
    FNR == NR { built[key($0)] = 1; next }
    !(key($0) in built) { print }
' "$work/new" "$work/old" > "$work/earlier"

# The index, its hashes, and the signatures: inline for apt, and detached for older tools
mkdir -p "$out"
cat "$work/new" "$work/earlier" > "$out/Packages"
gzip -9nc "$out/Packages" > "$out/Packages.gz"
mkdir -p "$work/index"
cp "$out/Packages" "$out/Packages.gz" "$work/index/"
(cd "$work/index" && apt-ftparchive \
    -o APT::FTPArchive::Release::Origin="Uniform Chat" \
    -o APT::FTPArchive::Release::Label="Uniform Chat" \
    -o APT::FTPArchive::Release::Suite="latest/download/" \
    -o APT::FTPArchive::Release::Description="Uniform Chat apps and servers for Debian and Ubuntu" \
    release .) > "$out/Release"
gpg --batch --yes --pinentry-mode loopback --passphrase-fd 0 --clearsign -o "$out/InRelease" "$out/Release" <<< "${APT_GPG_PASSPHRASE:-}"
gpg --batch --yes --pinentry-mode loopback --passphrase-fd 0 --armor --detach-sign -o "$out/Release.gpg" "$out/Release" <<< "${APT_GPG_PASSPHRASE:-}"

# Done: the public key, for /etc/apt/keyrings
gpg --batch --export > "$out/uniform-chat-archive-keyring.gpg"
