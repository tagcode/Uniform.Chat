#!/bin/sh
# Add Uniform Chat to the application menu of this user: a desktop entry and an icon pointing at this folder.
# Run it again after moving the folder; remove with: ./install-desktop-entry.sh --uninstall
set -e
here="$(cd "$(dirname "$0")" && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
entry="$data/applications/rip.uniform.chat.desktop"
icon="$data/icons/hicolor/256x256/apps/rip.uniform.chat.png"

# Uninstall
if [ "$1" = "--uninstall" ]; then
  rm -f "$entry" "$icon"
  echo "Removed Uniform Chat from the application menu."
  exit 0
fi

# Icon and entry
mkdir -p "$(dirname "$entry")" "$(dirname "$icon")"
cp "$here/uniform-chat.png" "$icon"
cat > "$entry" <<ENTRY
[Desktop Entry]
Type=Application
Name=Uniform Chat
GenericName=Matrix chat
Comment=Chat on Matrix
Exec="$here/Uniform.Chat"
Icon=rip.uniform.chat
Terminal=false
Categories=Network;InstantMessaging;Chat;
Keywords=Matrix;chat;messaging;
ENTRY
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" || true
echo "Added Uniform Chat to the application menu."
