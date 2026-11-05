#!/bin/sh
# MangaJaNai GUI (Linux tarball) installer.
#
# Run from inside the extracted folder: `./install.sh`. It does NOT move the folder - it registers the
# in-place tree with your desktop:
#   - a launcher symlink in ~/.local/bin (mangajanai-gui)
#   - a .desktop menu entry (~/.local/share/applications)
# then installs the components for the detected GPU (TensorRT runtime and kernels, the default models)
# into the tree. The tree stays writable, so the in-app updater and component manager keep working.
# Uninstall with ./uninstall.sh (the tree itself is removed by deleting it).
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
APP="$HERE/MangaJaNaiConverterGui"
BIN="${XDG_BIN_HOME:-$HOME/.local/bin}"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"

if [ ! -x "$APP" ]; then
    echo "error: MangaJaNai GUI not found at $APP (run this from the extracted folder)" >&2
    exit 1
fi

mkdir -p "$BIN" "$APPS" "$ICONS"
ln -sf "$APP" "$BIN/mangajanai-gui"

icon_line="Icon=mangajanai-gui"
if [ -f "$HERE/mangajanai-gui.png" ]; then
    cp -f "$HERE/mangajanai-gui.png" "$ICONS/mangajanai-gui.png"
else
    icon_line=""
fi

cat > "$APPS/mangajanai-gui.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=MangaJaNai GUI
Comment=Upscale manga and images with ONNX models on NVIDIA TensorRT
Exec=$APP
TryExec=$APP
Path=$HERE
$icon_line
Terminal=false
Categories=Graphics;
StartupNotify=true
EOF
update-desktop-database "$APPS" 2>/dev/null || true

echo "Installed:"
echo "  launcher : $BIN/mangajanai-gui  ->  $APP"
echo "  menu     : $APPS/mangajanai-gui.desktop"
echo
echo "Installing the components for this GPU (TensorRT runtime and kernels, the default models)..."
"$HERE/MangaJaNaiUpdater" --auto || echo "Component install failed; MangaJaNai GUI offers it again on first launch."
echo
case ":$PATH:" in
    *":$BIN:"*) ;;
    *) echo "NOTE: $BIN is not on your PATH; add it to use 'mangajanai-gui' from a shell." ;;
esac
echo "Launch 'MangaJaNai GUI' from your application menu, or: $APP"
