#!/bin/sh
# Packs a Linux build (the folder `dotnet publish -r linux-x64` wrote) as Muzikette-x86_64.AppImage and
# Muzikette-linux-x64.tar.gz.
#   packaging/linux/package.sh <build folder> <output folder>
# APPIMAGETOOL may name an appimagetool to use; otherwise the current one is downloaded.
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
build="$(cd "$1" && pwd)"
mkdir -p "$2"; out="$(cd "$2" && pwd)"
id=io.github.isasto.Muzikette
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT

# The desktop file, AppStream metadata and icons, under <prefix>/share.
share() {
  install -Dm644 "$here/$id.desktop" "$1/share/applications/$id.desktop"
  install -Dm644 "$here/$id.metainfo.xml" "$1/share/metainfo/$id.metainfo.xml"
  mkdir -p "$1/share/icons"; cp -a "$here/icons/hicolor" "$1/share/icons/"
}

# AppImage: the app in usr/lib/muzikette, started by the launcher as AppRun.
app="$work/Muzikette.AppDir"
mkdir -p "$app/usr/lib/muzikette"
cp -a "$build/." "$app/usr/lib/muzikette/"
install -m755 "$here/muzikette.sh" "$app/AppRun"
share "$app/usr"
cp "$here/$id.desktop" "$app/"
cp "$here/icons/hicolor/256x256/apps/$id.png" "$app/$id.png"
ln -s "$id.png" "$app/.DirIcon"
# NuGet unpacks the native libraries readable by their owner only; everyone who runs the app needs to read them.
chmod -R a+rX "$app"
tool="${APPIMAGETOOL:-}"
if [ -z "$tool" ]; then
  tool="$work/appimagetool"
  curl -fsSL -o "$tool" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$tool"
fi
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream "$app" "$out/Muzikette-x86_64.AppImage"

# tar.gz: the same files in one folder, started by ./muzikette, with the desktop file and icons under share/.
dir="$work/Muzikette"
mkdir -p "$dir"
cp -a "$build/." "$dir/"
install -m755 "$here/muzikette.sh" "$dir/muzikette"
share "$dir"
chmod -R a+rX "$dir"
tar -C "$work" --owner=0 --group=0 -czf "$out/Muzikette-linux-x64.tar.gz" Muzikette
ls -l "$out"
