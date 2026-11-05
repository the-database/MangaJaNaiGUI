#!/bin/sh
# Removes the desktop integration created by install.sh. The folder itself is left in place - delete
# it to remove the rest.
set -eu

BIN="${XDG_BIN_HOME:-$HOME/.local/bin}"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"

rm -f "$BIN/mangajanai-gui"
rm -f "$APPS/mangajanai-gui.desktop"
rm -f "$ICONS/mangajanai-gui.png"
update-desktop-database "$APPS" 2>/dev/null || true

echo "Removed the launcher symlink and the menu entry."
echo "Delete this folder to remove MangaJaNai GUI itself; settings, your models and cached"
echo "engines in ${XDG_CONFIG_HOME:-$HOME/.config}/MangaJaNaiConverterGui remain."
