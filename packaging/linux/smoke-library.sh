#!/bin/sh
# Writes a small library for the Linux launch checks: two albums of short tones, one with cover art and one without.
#   packaging/linux/smoke-library.sh <folder>      (needs ffmpeg)
set -eu
root="$1"
with="$root/Test Artist/With Cover"; without="$root/Test Artist/Without Cover"
mkdir -p "$with" "$without"
ffmpeg -v error -y -f lavfi -i "color=c=0x6a5acd:s=600x600" -frames:v 1 "$root/cover.jpg"
for n in 1 2 3; do
  ffmpeg -v error -y -f lavfi -i "sine=frequency=$((330 + n * 110)):duration=1" -i "$root/cover.jpg" -map 0:a -map 1:v \
    -c:a libmp3lame -c:v copy -id3v2_version 3 -disposition:v attached_pic \
    -metadata title="Tone $n" -metadata artist="Test Artist" -metadata album_artist="Test Artist" \
    -metadata album="With Cover" -metadata track="$n" -metadata date=2026 "$with/0$n Tone $n.mp3"
  ffmpeg -v error -y -f lavfi -i "sine=frequency=$((220 + n * 110)):duration=1" -c:a flac \
    -metadata title="Tone $n" -metadata artist="Test Artist" -metadata ALBUMARTIST="Test Artist" \
    -metadata album="Without Cover" -metadata track="$n" -metadata date=2025 "$without/0$n Tone $n.flac"
done
rm "$root/cover.jpg"
