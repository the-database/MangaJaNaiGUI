#!/bin/bash
# Builds the MangaJaNai GUI AppImage from an assembled linux-x64 install tree (the core). An AppImage
# can't install component packs into its read-only squashfs: on first launch AppRun has the image's
# aji-runtime install the TensorRT runtime, this GPU's builder pack and the models into the shared
# pack store instead.
#
# Usage: build-appimage.sh <tree-dir> <version> [out-dir]
#
# The app is a self-contained .NET/Avalonia publish, so there is no dependency walk to do: the host
# provides glibc, the X11/Wayland + GL stack, fontconfig and the NVIDIA driver. Runs headless via
# APPIMAGE_EXTRACT_AND_RUN (no FUSE needed on CI runners).
set -euo pipefail

TREE="$1"
VER="$2"
OUT="${3:-.}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
APPDIR="$WORK/AppDir"
export APPIMAGE_EXTRACT_AND_RUN=1
export ARCH=x86_64

[ -d "$TREE" ] || { echo "tree not found: $TREE" >&2; exit 1; }

AT="$(command -v appimagetool || true)"
if [ -z "$AT" ]; then
    AT="$WORK/appimagetool-x86_64.AppImage"
    curl -fsSL "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage" -o "$AT"
    chmod +x "$AT"
fi

mkdir -p "$APPDIR"
cp -a "$TREE/." "$APPDIR/"
# The AppImage updates by being replaced: no in-place updater, and no tarball install scripts.
rm -f "$APPDIR/MangaJaNaiUpdater" "$APPDIR/install.sh" "$APPDIR/uninstall.sh"
install -m755 "$HERE/AppRun" "$APPDIR/AppRun"
# (trtexec comes from the store's TensorRT pack, not the tree)
chmod +x "$APPDIR/MangaJaNaiConverterGui" "$APPDIR/7zz" "$APPDIR/inference/mangajanai_upscale" \
         "$APPDIR/aji-runtime"

cp "$HERE/../../logo.png" "$APPDIR/mangajanai-gui.png"
cat > "$APPDIR/mangajanai-gui.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=MangaJaNai GUI
Comment=Upscale manga and images with ONNX models on NVIDIA TensorRT
Exec=AppRun
Icon=mangajanai-gui
Terminal=false
Categories=Graphics;
EOF

mkdir -p "$OUT"
outfile="$OUT/MangaJaNaiGUI-x86_64-v${VER}.AppImage"
"$AT" "$APPDIR" "$outfile"
echo "$outfile"
rm -rf "$WORK"
