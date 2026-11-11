#!/bin/bash
# Build the .deb of one Linux app from its published, self-contained folder: the app in /opt/uniform-chat/<kind>, its command
# in /usr/bin; the desktop app also gets a menu entry and an icon, the server a systemd service run by the user uniform-chat.
# Usage: build-deb.sh <desktop|terminal|server|stun|livekit> <published folder> <version> <linux-x64|linux-arm64> <output folder>
# The output is <package>_<version>_<architecture>.deb, e.g. uniform-chat-server_0.2.0_amd64.deb.
set -euo pipefail
kind=$1 source=$2 version=$3 rid=$4 out=$5
here="$(cd "$(dirname "$0")" && pwd)"

# Debian's names: amd64 and arm64; a prerelease sorts before its release with a tilde, e.g. 0.2.0~rc.1 before 0.2.0
case "$rid" in
    linux-x64) arch=amd64 ;;
    linux-arm64) arch=arm64 ;;
    *) echo "Unknown runtime $rid" >&2; exit 1 ;;
esac
debversion="${version//-/\~}"

# What .NET needs of the system without its runtime: OpenSSL and ICU under the names of Debian 12 and 13, and Ubuntu 22.04 and later
runtime="libc6, libgcc-s1, libstdc++6, zlib1g, libssl3t64 | libssl3, libicu78 | libicu77 | libicu76 | libicu75 | libicu74 | libicu72 | libicu70"
recommends=""
case "$kind" in
    desktop)
        package=uniform-chat command=uniform-chat program=Uniform.Chat
        depends="$runtime, libx11-6, libice6, libsm6, libfontconfig1"
        recommends="libasound2t64 | libasound2"
        summary="Matrix chat app with voice calls"
        details="Uniform Chat is a chat app for Matrix, the open network for secure, decentralized communication: rooms, direct chats, end-to-end encryption, files and voice calls. Run it with uniform-chat, or from the application menu."
        ;;
    terminal)
        package=uniform-chat-terminal command=uniform-chat-terminal program=Uniform.Chat
        depends="$runtime"
        recommends="libasound2t64 | libasound2"
        summary="Matrix chat client for the terminal"
        details="The Uniform Chat terminal client: rooms, direct chats, end-to-end encryption, files and voice calls in a text window. Run it with uniform-chat-terminal."
        ;;
    server)
        package=uniform-chat-server command=uniform-chat-server program=uniform-chat-server
        depends="$runtime, passwd"
        summary="Matrix homeserver with a terminal dashboard"
        details="The Uniform Chat homeserver keeps accounts, rooms and messages for Matrix apps, with a built-in TURN server for calls. The service uniform-chat-server runs it as the user uniform-chat, with settings in /etc/uniform-chat-server and data in /var/lib/uniform-chat-server; it is off until enabled."
        ;;
    stun)
        package=uniform-stun-server command=uniform-stun-server program=uniform-stun-server
        depends="$runtime"
        summary="STUN and TURN server for calls"
        details="A STUN and TURN server that relays voice calls through firewalls, with time-limited credentials shared with Matrix homeservers. Run it with uniform-stun-server."
        ;;
    livekit)
        package=uniform-livekit-server command=uniform-livekit-server program=uniform-livekit-server
        depends="$runtime"
        summary="MatrixRTC authorization service for LiveKit"
        details="Gives Element X, Element Call and other MatrixRTC apps tokens of a LiveKit server, after checking their users with their Matrix homeservers; a replacement of lk-jwt-service. Run it with uniform-livekit-server."
        ;;
    *) echo "Unknown app $kind" >&2; exit 1 ;;
esac

# The app as published, without the menu script of the archives, which the package replaces
root="$(mktemp -d)"
trap 'rm -rf "$root"' EXIT
app="$root/opt/uniform-chat/$kind"
mkdir -p "$app" "$root/usr/bin" "$root/usr/share/doc/$package" "$root/DEBIAN"
cp -a "$source/." "$app/"
rm -f "$app/install-desktop-entry.sh"
ln -s "/opt/uniform-chat/$kind/$program" "$root/usr/bin/$command"
cp "$source/LICENSE" "$root/usr/share/doc/$package/copyright"

# Menu entry and icon; the service and its scripts
if [[ "$kind" == desktop ]]; then
    install -D -m 0644 "$here/rip.uniform.chat.desktop" "$root/usr/share/applications/rip.uniform.chat.desktop"
    install -D -m 0644 "$source/uniform-chat.png" "$root/usr/share/icons/hicolor/256x256/apps/rip.uniform.chat.png"
fi
if [[ "$kind" == server ]]; then
    install -D -m 0644 "$here/uniform-chat-server.service" "$root/usr/lib/systemd/system/uniform-chat-server.service"
    for script in postinst prerm postrm; do install -m 0755 "$here/server.$script" "$root/DEBIAN/$script"; done
fi

# Readable by all, the programs runnable
find "$root" -type d -exec chmod 0755 {} +
find "$root/opt" "$root/usr/share" -type f -exec chmod 0644 {} +
chmod 0755 "$app/$program"
find "$app" -type f -name '*.so*' -exec chmod 0755 {} +

# Control file
{
    echo "Package: $package"
    echo "Version: $debversion"
    echo "Architecture: $arch"
    echo "Maintainer: tagcode <tagcode@users.noreply.github.com>"
    echo "Installed-Size: $(du -sk --exclude=DEBIAN "$root" | cut -f1)"
    echo "Depends: $depends"
    [[ -n "$recommends" ]] && echo "Recommends: $recommends"
    echo "Section: net"
    echo "Priority: optional"
    echo "Homepage: https://github.com/tagcode/Uniform.Chat"
    echo "Description: $summary"
    echo "$details" | fold -s -w 78 | sed 's/ *$//; s/^/ /'
} > "$root/DEBIAN/control"

# Done
mkdir -p "$out"
dpkg-deb --build --root-owner-group -Zxz "$root" "$out/${package}_${debversion}_${arch}.deb"
