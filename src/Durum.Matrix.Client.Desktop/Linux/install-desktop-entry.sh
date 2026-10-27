#!/bin/sh
# Add Durum to the application menu of this user: a desktop entry and an icon pointing at this folder.
# Run it again after moving the folder; remove with: ./install-desktop-entry.sh --uninstall
set -e
here="$(cd "$(dirname "$0")" && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
entry="$data/applications/io.github.tagcode.durum.desktop"
icon="$data/icons/hicolor/256x256/apps/io.github.tagcode.durum.png"

# Uninstall
if [ "$1" = "--uninstall" ]; then
  rm -f "$entry" "$icon"
  echo "Removed Durum from the application menu."
  exit 0
fi

# Icon and entry
mkdir -p "$(dirname "$entry")" "$(dirname "$icon")"
cp "$here/durum.png" "$icon"
cat > "$entry" <<ENTRY
[Desktop Entry]
Type=Application
Name=Durum
GenericName=Matrix chat
Comment=Chat on Matrix
Exec="$here/Durum.Matrix.Client.Desktop"
Icon=io.github.tagcode.durum
Terminal=false
Categories=Network;InstantMessaging;Chat;
Keywords=Matrix;chat;messaging;
ENTRY
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" || true
echo "Added Durum to the application menu."
