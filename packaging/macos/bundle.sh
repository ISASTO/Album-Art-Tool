#!/bin/sh
# Builds Muzikette.app, one universal program for Apple Silicon and Intel Macs, and Muzikette.dmg from the two folders
# that `dotnet publish -r osx-arm64` and `dotnet publish -r osx-x64` wrote. Runs on macOS (lipo, iconutil, codesign,
# hdiutil come with Xcode's command line tools).
#   packaging/macos/bundle.sh <arm64 build> <x64 build> <output folder> <version>
# The app is signed ad hoc, or with MACOS_SIGN_IDENTITY (a "Developer ID Application" certificate in the keychain,
# with the hardened runtime, ready for notarization) when that is set.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
arm=$(cd "$1" && pwd)
x64=$(cd "$2" && pwd)
mkdir -p "$3"; out=$(cd "$3" && pwd)
version=$4
identity="${MACOS_SIGN_IDENTITY:--}"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT

app="$work/Muzikette.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

# Programs and libraries: Mach-O files present in both builds are joined into one universal file (the libraries from
# NuGet are usually universal already). Debug symbols stay out.
for file in "$arm"/*; do
  name=$(basename "$file")
  case "$name" in *.dSYM|*.dbg|*.pdb) continue ;; esac
  [ -f "$file" ] || continue
  if lipo -info "$file" >/dev/null 2>&1; then
    if lipo "$file" -verify_arch arm64 x86_64 2>/dev/null; then
      cp "$file" "$app/Contents/MacOS/$name"
    elif [ -f "$x64/$name" ]; then
      lipo -create "$file" "$x64/$name" -output "$app/Contents/MacOS/$name"
    else
      echo "$name is missing from the Intel build" >&2; exit 1
    fi
  else
    cp "$file" "$app/Contents/Resources/$name"
  fi
done
lipo "$app/Contents/MacOS/Muzikette" -verify_arch arm64 x86_64
chmod 755 "$app/Contents/MacOS/Muzikette"

# Bundle details, the icon and the license notices.
sed "s/@VERSION@/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist"
printf 'APPL????' > "$app/Contents/PkgInfo"
iconutil -c icns "$here/Muzikette.iconset" -o "$app/Contents/Resources/Muzikette.icns"
cp "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.txt" "$app/Contents/Resources/"
cp -R "$root/licenses" "$app/Contents/Resources/licenses"

# Signing: libraries first, then the app.
if [ "$identity" = "-" ]; then
  codesign --force --deep --sign - "$app"
else
  for library in "$app/Contents/MacOS/"*.dylib; do
    codesign --force --timestamp --options runtime --sign "$identity" "$library"
  done
  codesign --force --timestamp --options runtime --sign "$identity" "$app"
fi
codesign --verify --deep --strict --verbose=2 "$app"

# The disk image: the app and a link to Applications to drag it onto.
stage="$work/dmg"
mkdir -p "$stage"
ditto "$app" "$stage/Muzikette.app"
ln -s /Applications "$stage/Applications"
rm -f "$out/Muzikette.dmg"
hdiutil create -volname "Muzikette" -srcfolder "$stage" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$out/Muzikette.dmg"
if [ "$identity" != "-" ]; then codesign --force --timestamp --sign "$identity" "$out/Muzikette.dmg"; fi

# A copy of the app beside the image, for the launch checks.
rm -rf "$out/Muzikette.app"
ditto "$app" "$out/Muzikette.app"
ls -l "$out"
