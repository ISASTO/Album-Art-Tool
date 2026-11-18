#!/bin/sh
# Starts Muzikette from an AppImage (as AppRun), a Flatpak (/app/bin/muzikette) or an unpacked folder.
here="$(dirname "$(readlink -f "$0")")"
for program in "$here/usr/lib/muzikette/Muzikette" "$here/../lib/muzikette/Muzikette" "$here/Muzikette"; do
  [ -x "$program" ] && break
done
# .NET uses the system's ICU for languages and sorting; without it, it runs in invariant mode instead of refusing to start.
icu=0
for dir in /usr/lib /usr/lib64 /usr/lib/x86_64-linux-gnu /lib/x86_64-linux-gnu /app/lib; do
  if ls "$dir"/libicuuc.so.* >/dev/null 2>&1; then icu=1; break; fi
done
[ "$icu" = 1 ] || export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
exec "$program" "$@"
