#!/usr/bin/env python3
"""Builds a stand-in music library of real albums for the README screenshots.

Run by the "showcase" job in .github/workflows/windows.yml (manual runs only). Each album's cover, release year and
track list come from Deezer's public API (Apple's as a fallback) at build time. The audio is silence of each track's
real length. Nothing this script downloads is committed: the library exists only on the build machine, and only the
finished screenshots are kept.

usage: library.py <library folder> <covers folder>
"""
import io
import json
import os
import re
import subprocess
import sys
import time
import unicodedata
import urllib.parse
import urllib.request

from mutagen.flac import FLAC, Picture
from mutagen.id3 import APIC, ID3, TALB, TDRC, TIT2, TPE1, TPE2, TRCK
from PIL import Image

# (artist, album, how it is stored). Formats and gaps are chosen so every page has something to show:
#   flac / mp3-320 / mp3-128   audio format (128 kbps shows up under Health › Low bitrate)
#   cover      every track has the cover embedded (600 px)
#   none       no cover anywhere (Missing art)
#   partial    only the first half of the tracks has the cover
#   folder     cover.jpg in the folder, nothing embedded
#   large      the full-size cover embedded (Health › Large covers), one track without a title (Missing titles)
ALBUMS = [
    ("Sophia Stel", "the greatest album of all time", "flac", "cover"),
    ("Masakatsu Takagi", "Kagayaki", "flac", "cover", ["かがやき"]),
    ("KUNZITE", "SECRETS", "mp3-320", "cover"),
    ("Pomme", "consolation", "flac", "cover"),
    ("Pomme", "Saisons", "flac", "none"),
    ("Pomme", "la forteresse", "flac", "cover"),
    ("Ayegy", "Evangeline", "flac", "partial"),
    ("Pearly Drops", "Call For Help", "flac", "cover"),
    ("Pearly Drops", "A Little Disaster", "mp3-128", "cover"),
    ("Pearly Drops", "The Voices Are Coming Back", "flac", "none"),
    ("After", "Fell Asleep in the Sun", "flac", "cover"),
    ("Sega Bodega", "Dennis", "flac", "cover"),
    ("Sega Bodega", "Romeo", "flac", "cover"),
    ("Sega Bodega", "Salvador", "flac", "folder"),
    ("Ear", "The Most Dear and The Future", "flac", "cover"),
    ("Orbital", "Monsters Exist", "flac", "large"),
]


def get(url, binary=False):
    for attempt in range(4):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "Muzikette-showcase/1.0 (+https://github.com/ISASTO/Muzikette)"})
            with urllib.request.urlopen(request, timeout=30) as response:
                data = response.read()
                return data if binary else json.loads(data.decode("utf-8"))
        except Exception as error:  # network hiccups and rate limits: wait and retry
            if attempt == 3:
                raise
            print(f"  retry {url}: {error}")
            time.sleep(2 + attempt * 3)


def norm(text):
    """Lower case, accents and punctuation removed; letters of every script are kept (かがやき stays かがやき)."""
    text = "".join(c for c in unicodedata.normalize("NFKD", text or "") if not unicodedata.combining(c)).casefold()
    text = re.sub(r"\((deluxe|expanded|remaster(ed)?|bonus)[^)]*\)|\[[^\]]*\]", "", text)
    return re.sub(r"[\W_]+", " ", text).strip()


def best(results, artists, titles, artist_of, title_of):
    artists = {norm(a) for a in artists}; titles = [norm(t) for t in titles if norm(t)]
    exact = [r for r in results if norm(artist_of(r)) in artists and norm(title_of(r)) in titles]
    close = [r for r in results if norm(artist_of(r)) in artists and any(norm(title_of(r)).startswith(t) for t in titles)]
    return (exact or close or [None])[0]


def deezer(artist, title, aliases=()):
    titles = [title] + list(aliases)
    pick = None
    for name in titles:
        for query in (f'artist:"{artist}" album:"{name}"', f"{artist} {name}"):
            results = get("https://api.deezer.com/search/album?q=" + urllib.parse.quote(query) + "&limit=25").get("data", [])
            pick = best(results, [artist], titles, lambda r: r["artist"]["name"], lambda r: r["title"])
            if pick:
                break
        if pick:
            break
    if pick is None:
        # Search indexes miss some releases; the artist's own discography does not.
        for found in get("https://api.deezer.com/search/artist?q=" + urllib.parse.quote(artist) + "&limit=5").get("data", []):
            if norm(found["name"]) != norm(artist):
                continue
            releases = get(f"https://api.deezer.com/artist/{found['id']}/albums?limit=200").get("data", [])
            pick = best(releases, [artist], titles, lambda r: artist, lambda r: r["title"])
            if pick:
                break
    if pick is None:
        return None
    album = get(f"https://api.deezer.com/album/{pick['id']}")
    tracks = album.get("tracks", {}).get("data", [])
    if not tracks:
        return None
    out = {"source": "Deezer", "url": album.get("link"), "artist": album["artist"]["name"], "title": album["title"],
           "year": (album.get("release_date") or "")[:4], "cover": album.get("cover_xl"), "tracks": []}
    for number, track in enumerate(tracks, 1):
        out["tracks"].append({"number": number, "title": track["title"], "seconds": int(track.get("duration") or 180),
                              "artist": track.get("artist", {}).get("name") or album["artist"]["name"], "id": track.get("id")})
    return out


def apple(artist, title, aliases=()):
    pick = None
    for name in [title] + list(aliases):
        results = get("https://itunes.apple.com/search?term=" + urllib.parse.quote(f"{artist} {name}") + "&entity=album&limit=25").get("results", [])
        pick = best(results, [artist], [title] + list(aliases), lambda r: r.get("artistName"), lambda r: r.get("collectionName"))
        if pick:
            break
    if pick is None:
        return None
    songs = [r for r in get(f"https://itunes.apple.com/lookup?id={pick['collectionId']}&entity=song").get("results", []) if r.get("wrapperType") == "track"]
    songs.sort(key=lambda s: (s.get("discNumber", 1), s.get("trackNumber", 0)))
    out = {"source": "Apple Music", "url": pick.get("collectionViewUrl"), "artist": pick["artistName"], "title": pick["collectionName"],
           "year": (pick.get("releaseDate") or "")[:4], "cover": pick["artworkUrl100"].replace("100x100bb", "1000x1000bb"), "tracks": []}
    for number, song in enumerate(songs, 1):
        out["tracks"].append({"number": number, "title": song["trackName"], "seconds": int((song.get("trackTimeMillis") or 180000) / 1000),
                              "artist": song.get("artistName") or pick["artistName"]})
    return out if out["tracks"] else None


def contributors(album):
    """Featured artists, from Deezer's per-track details (the album listing names only the main artist)."""
    if album["source"] != "Deezer":
        return
    for track in album["tracks"]:
        if not track.get("id"):
            continue
        detail = get(f"https://api.deezer.com/track/{track['id']}")
        names = [c["name"] for c in detail.get("contributors", []) if c.get("role") in (None, "Main", "Featured")]
        if len(names) > 1:
            track["artist"] = ", ".join(dict.fromkeys(names))


def safe(name):
    return re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name).strip(" .") or "_"


def silence(path, seconds, kind):
    codec = ["-c:a", "flac", "-compression_level", "8"] if kind == "flac" else ["-c:a", "libmp3lame", "-b:a", kind.split("-")[1] + "k"]
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", str(max(1, seconds))] + codec + [path], check=True)


def tag(path, kind, album, track, picture):
    artist = track["artist"]
    if kind == "flac":
        audio = FLAC(path)
        if track["title"]:
            audio["title"] = track["title"]
        audio["artist"] = artist; audio["albumartist"] = album["artist"]; audio["album"] = album["title"]
        audio["tracknumber"] = str(track["number"]); audio["date"] = album["year"]
        if picture:
            cover = Picture(); cover.type = 3; cover.mime = "image/jpeg"; cover.data = picture
            with Image.open(io.BytesIO(picture)) as image:
                cover.width, cover.height, cover.depth = image.width, image.height, 24
            audio.add_picture(cover)
        audio.save()
    else:
        audio = ID3()
        if track["title"]:
            audio.add(TIT2(encoding=3, text=track["title"]))
        audio.add(TPE1(encoding=3, text=artist)); audio.add(TPE2(encoding=3, text=album["artist"])); audio.add(TALB(encoding=3, text=album["title"]))
        audio.add(TRCK(encoding=3, text=str(track["number"]))); audio.add(TDRC(encoding=3, text=album["year"]))
        if picture:
            audio.add(APIC(encoding=3, mime="image/jpeg", type=3, desc="Cover", data=picture))
        audio.save(path)


def jpeg(data, side=None):
    with Image.open(io.BytesIO(data)) as image:
        image = image.convert("RGB")
        if side:
            image = image.resize((side, side), Image.LANCZOS)
        out = io.BytesIO(); image.save(out, "JPEG", quality=90)
        return out.getvalue()


def main(library, covers):
    os.makedirs(library, exist_ok=True); os.makedirs(covers, exist_ok=True)
    manifest = []
    for index, (artist, title, kind, art, *rest) in enumerate(ALBUMS):
        aliases = rest[0] if rest else []
        album = None
        for lookup in (deezer, apple):
            try:
                album = lookup(artist, title, aliases)
            except Exception as error:
                print(f"  {lookup.__name__} failed for {artist} - {title}: {error}")
            if album:
                break
        if not album or not album.get("cover"):
            print(f"MISSING  {artist} - {title}: not found, left out")
            continue
        if artist == "Sega Bodega":
            try:
                contributors(album)
            except Exception as error:
                print(f"  contributors failed: {error}")
        original = jpeg(get(album["cover"], binary=True))
        cover_file = os.path.join(covers, f"{index:02d}.jpg")
        open(cover_file, "wb").write(original)
        embedded = None if art in ("none", "folder") else original if art == "large" else jpeg(original, 600)
        folder = os.path.join(library, safe(album["artist"]), safe(album["title"]))
        os.makedirs(folder, exist_ok=True)
        extension = ".flac" if kind == "flac" else ".mp3"
        half = (len(album["tracks"]) + 1) // 2
        for track in album["tracks"]:
            if art == "large" and track["number"] == 3:
                track["title"] = ""
            path = os.path.join(folder, f"{track['number']:02d} {safe(track['title'] or 'Track ' + str(track['number']))}{extension}")
            silence(path, track["seconds"], kind)
            picture = embedded if art != "partial" or track["number"] <= half else None
            tag(path, kind, album, track, picture)
        if art == "folder":
            open(os.path.join(folder, "cover.jpg"), "wb").write(jpeg(original, 600))
        minutes = sum(t["seconds"] for t in album["tracks"]) // 60
        featured = sum(1 for t in album["tracks"] if t["artist"] != album["artist"])
        print(f"FOUND    {artist} - {title}  =>  {album['source']}: {album['artist']} - {album['title']} ({album['year']}), "
              f"{len(album['tracks'])} tracks, {minutes} min, {featured} featuring, {album['url']}")
        manifest.append({"artist": album["artist"], "title": album["title"], "year": album["year"], "folder": folder,
                         "cover": cover_file, "source": album["source"], "art": art, "format": kind})
    with open(os.path.join(covers, "albums.json"), "w", encoding="utf-8") as out:
        json.dump(manifest, out, ensure_ascii=False, indent=1)
    print(f"{len(manifest)} of {len(ALBUMS)} albums ready in {library}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
