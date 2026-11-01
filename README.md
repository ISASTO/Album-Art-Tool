# Album Art Tool

A small, portable Windows app for album covers, duplicate scanning, and track artist cleanup, with built-in online artwork search.

**[Download the Windows app](https://github.com/ISASTO/Album-Art-Tool/releases/latest)** · Single portable EXE.

![Album Art Tool on Windows, showing online artwork suggestions](docs/screenshot.png)

*Interface shown with test albums and example search results.*

## Use it

1. Download **AlbumArtTool.exe** from Releases. Put it in the top folder of your music collection and double-click it. You can also keep it elsewhere and use **Choose folder**.
2. The app automatically scans that folder and every subfolder.
3. **Add missing album art** shows albums where one or more tracks lack an embedded front cover. **Change existing album art** shows albums with embedded or folder artwork.
4. Select an album. Online suggestions automatically search its **album title + artist name** and display cover choices inside the app.
5. **Click a cover or its Apply this cover button to add it to the work queue.** Continue selecting other albums while covers download and save in the background. Backups and Undo work for online changes too.

You can still drag a JPG, PNG, BMP or GIF from File Explorer onto an album row or the small cover preview, or use **Choose image**. Local images are staged first; click the larger **Apply cover** button to apply them.

If an album already has a folder cover, **Apply cover to X tracks** is enabled immediately. It uses the full-size image behind the preview, so you do not need to choose that file again. The same shortcut can copy the displayed embedded cover to tracks that are missing it.

The **Queue** button shows active and waiting albums, recent results, and **Clear waiting jobs**. Albums in the list are marked **Queued** or **Applying cover…**. Jobs run one at a time; each keeps the album, image, and checkbox settings you chose when adding it. A failed job does not block later albums. You can browse both tabs, search, stage images and queue more work throughout. Folder switching, rescanning and Undo are available when the queue finishes. To close with work pending, clear waiting jobs and let the current update finish.

No installer, accounts, API keys or background service. Online suggestions use the internet; uncheck **Online suggestions** to work offline. Windows 10 (1903 or newer) and Windows 11 use the .NET Framework 4.8 runtime already included with Windows. The EXE embeds its only third-party library, TagLibSharp. The ZIP includes the same EXE and license notices.

Scanning first counts supported music files using directory listings, without reading audio tags. It then shows a filling progress bar, a percentage, and an exact counter such as **2751/3867 tracks scanned**. Files that fail to open still count as checked and appear in Details. The initial counting phase can also be stopped. Disabled options keep readable text on the dark background.

[Scan progress preview](docs/scanning.png) · [Background queue preview](docs/queue.png)

## Track artist cleanup

Open **Track artists** to find albums whose track credits can be collapsed to the main artist. This tab includes albums with and without cover art. **Show → Artist cleanup available** lists albums with suggested changes; **Show → All albums in this tab** lets you review any album and enter an artist manually. **Sort → Artist changes first** puts albums with the most changes at the top. Artist/album and album-title sorting are also available.

Select an album, review its current track artists, and edit **Main artist** if necessary. Click **Set artists on X tracks** to queue the change, then continue with another album. For your BENEE example, tracks credited to BENEE and guests become a single **BENEE** credit. The change sets both **Artist** and **Album artist** to the entered name; existing artist sort fields are updated too. Track titles, artwork, audio and other tags stay as they are.

The suggestion uses a specific tagged album artist first, or a consistent lead artist across the tracks when album artist is missing or says Various Artists. A consistent band name such as **Earth, Wind & Fire** stays intact. Mixed-artist compilations without a consistent lead get no automatic suggestion; you can review them with the all-albums filter. Review inferred names before queueing.

Artist jobs share the artwork queue, freeze the selected album/name at enqueue time, and use the same full-file backups, stale-file protection and **Undo last change**. Already-correct tracks are skipped. Failed writes are listed in Details. Artist cleanup does not rename or move files/folders. The cleanup filter removes an album once its artist fields match.

## Find duplicate songs and albums

After scanning a music folder, click **Find duplicates**. A separate window scans the successfully read tracks in that folder tree, with a progress bar, percentage, checked/total counter and Stop button. Album browsing remains available. **Open selected folder** takes you to a result's location, and **Save report** exports a CSV with paths and the evidence for each match. Start a new scan from the main window after rescanning the library to include newly added music.

- **Confirmed song copies** have identical SHA-256 hashes of the entire file, or identical encoded MP3/FLAC audio data. MP3 tag blocks and FLAC metadata/artwork blocks are excluded from the audio comparison. Hover over a match to see which test confirmed it.
- **Confirmed album copies** have the same complete set of verified song contents in different folders. A folder containing only part of an album is not labeled a complete copy. Repeated tracks count separately when comparing album contents.
- **Possible song copies** match a tagged title, track artist and duration within two seconds. This can find different encodings and bitrates, but it can also include different recordings. **Possible album copies** match album/artist, track count, song titles, artists and durations. These require review.

The scanner only reads music. It does **not** delete, move or select copies for removal. Repeated music on a compilation can be an intentional duplicate. Other codecs get whole-file verification and metadata comparisons; they do not get a claim of identical audio across retagging or re-encoding. MP3 and FLAC audio checks compare encoded data, not perceptual fingerprints, so a re-encode can appear only as a possible match. Missing, linked or changed files are skipped with an explanation. Backups and temporary editor files remain excluded.

The first pass uses small audio samples and file sizes to find candidates; full hashes are then calculated for candidate files, rather than reading every complete song by default. The window uses a snapshot of the main library scan. If you change tags or covers while it is working, changed files are skipped; rescan the library and reopen the duplicate scanner to get current results. App installation waits for an active duplicate scan before restarting.

## App updates

Click **Get updates** in the header. The app checks this repository's latest published stable release, downloads a newer EXE with progress, verifies GitHub's SHA-256 checksum and the EXE's version, then restarts in the same music folder. If you are already current, it reports your installed version. Checks only happen when you click the button; there is no background update service or account requirement.

You can keep working during the download. Installation waits for scans, Undo, and every queued artwork job to finish. If you have a staged cover, apply it or press Esc to discard it before the update can restart. The update window has a Cancel button until installation starts. As with any restart, the in-memory queue history and Undo session end; your music and saved artwork backups remain in place.

A temporary copy of the current app waits for the running EXE to exit, verifies the download again, replaces it while retaining a rollback copy, and starts the new version. The rollback copy is removed only after the new window confirms startup. If launch fails, the previous EXE is restored and reopened. If startup cannot be confirmed while the new process is still running, it is left running and the backup location is reported. Read-only folders, locked files, network errors, and invalid downloads produce an error without installing an unverified EXE. Automatic replacement requires a writable application folder and a filesystem that supports file replacement.

**Install v1.3.0 manually once to get the button** if you are upgrading from an earlier release. After that, use **Get updates** for future releases. No music files are sent to GitHub.

[Update window preview](docs/updates.png)

## Online cover search

- Only the selected album is searched. The query starts as `album title + artist name`; you can edit it and press Enter or Search if tags are incomplete or an edition needs clarification.
- Sources are searched independently, so one failing source cannot prevent the others from returning covers. Good title-and-artist matches rank first; **Bandcamp is preferred among similarly strong matches**, followed by high-resolution Deezer album artwork and front covers from MusicBrainz's Cover Art Archive.
- Bandcamp results depend on its public search page. If Bandcamp requires browser verification or blocks the request, the app reports that source as unavailable and stops retrying it for the session. The **Bandcamp ↗** button opens the same search in your browser. No CAPTCHA bypass, login or hidden API credentials are used.
- Cover Art Archive requests previews up to 250 pixels and front covers up to 1200 pixels; actual dimensions depend on the available image. Deezer uses its `cover_xl` image. Bandcamp uses the original cover asset when its public search results are available. The existing 1600-pixel embedded-art limit still applies.
- Each suggested cover shows the source image's resolution, such as **1000 × 1000 px**. Thumbnails appear first while the app checks the full-size image header. These checks read at most 128 KB per cover, with two running at a time. If a source cannot provide readable dimensions, the card says **Size unavailable**; the cover can still be selected.
- Source names beneath covers link to their album pages. Compare the title, artist and artwork before clicking, especially for similar titles or alternate releases.
- Results are cached for up to 24 queries during the current session. Rapid selection changes cancel stale searches. MusicBrainz requests are spaced at least 1.1 seconds apart; server rate-limit responses are respected.
- Searches send the displayed query to the sources, then download image previews and the chosen cover. Audio files and full local folder paths are never uploaded. Turning off automatic suggestions cancels the search; the Search button can still run an explicit one-time lookup.
- Offline connections, missing covers and failed downloads are shown inside the app. A failed download cannot change any music file. Local drag-and-drop remains available.

## What gets changed

- Artwork is embedded into the audio files. Audio is not re-encoded. Existing music metadata and explicitly typed back covers, booklet pages and disc pictures are preserved. Existing front-cover and unclassified artwork slots are replaced by the new front cover.
- On the missing-art tab, **Only fill tracks missing covers** starts checked. Tracks that already have an embedded cover are left alone. Uncheck it to give the whole album the same cover.
- On the existing-art tab, the default is to replace the cover on every track in the selected album.
- **Update folder cover images too** updates recognized `cover`, `folder`, `front`, `album` and `albumart` images in that album's folder, preserving their file format. If there are none, it creates `cover.jpg`. This option is disabled when multiple albums share the folder. Unrelated images are untouched.
- A folder image alone does not mean the tracks have embedded covers. Those albums appear in the missing-art tab too, with a **Folder art only** label.
- New embedded covers are JPEGs, at most 1600 pixels on the longest side, with their aspect ratio preserved. Images are never cropped or enlarged; transparency is flattened onto white. Inputs are limited to 30 MB and 40 million pixels.
- The existing-art preview shows the first readable embedded front cover, falling back to folder art. It does not imply that every track has the same cover.

## Albums and formats

Albums are grouped by **folder + album title + album artist**. Missing album tags fall back to the folder name. Track artist is not used as a grouping key, so compilations stay together. Separate folders and separate disc folders remain separate entries; this prevents one edition from changing another. Search also matches full folder paths.

Supported extensions: MP3, FLAC, M4A, M4B, OGG/OGA, Opus, WMA, WAV, AIF/AIFF, APE and WV. Files must have writable metadata supported by TagLibSharp. Unsupported or damaged files appear in **Details**, not as successfully processed tracks. The test suite exercises real MP3, FLAC, M4A, Ogg, Opus, WMA, WAV, AIFF and WavPack files.

Scans run in the background and can be stopped. Stopping keeps fully scanned folders; a partly scanned folder is excluded so it cannot be mistaken for a complete album. Directory junctions, symbolic links, system subfolders, backups and temporary copies are skipped. The app does not follow a link out of the selected music tree.

## Backups and Undo

Every audio update is written to a temporary copy, reopened to verify its artwork, and atomically swapped into place. The complete original is kept beside the album in:

```text
.album-art-backups/YYYYMMDD-HHMMSS-id/
```

**Undo last change** restores your most recent completed edit during the current app session, once the queue is idle. Backups remain after you close the app; to recover an older edit, close music players and copy the wanted original from its backup folder over the changed file. Backups contain full audio files and use disk space; you can delete them once satisfied with your edits.

Files changed since the scan, read-only files, and files locked against replacement are skipped with an explanation. Undo also refuses to overwrite a file edited by another program after this app's change. Changes are independent per file: if one track fails, successfully updated tracks stay updated and the remaining errors are listed in **Details**. Filesystems must support atomic file replacement; if they do not, the affected file is left unchanged.

## Keyboard

- **Ctrl+F**: search
- **F5**: rescan
- **Ctrl+Z**: undo last change
- **Esc**: discard the staged cover
- Arrow keys: select an album

## Build and test

On Windows, install the .NET 8 SDK, then run:

```powershell
dotnet build src/AlbumArtTool/AlbumArtTool.csproj -c Release
```

The portable executable is `src/AlbumArtTool/bin/Release/net48/AlbumArtTool.exe`. It can be copied on its own. To run the integration suite, install FFmpeg and point `ALBUMART_FFMPEG` at its executable:

```powershell
dotnet build tests/AlbumArtTool.Tests.csproj -c Release
$env:ALBUMART_FFMPEG = 'C:\path\to\ffmpeg.exe'
tests/bin/Release/net48/AlbumArtTool.Tests.exe TestResults
```

Tests also exercise provider parsing and ranking, unavailable sources, search caching, cancellation, bounded image-header checks, redirect validation, source resolutions, counted scan progress, direct folder-cover application, and the responsive work queue. Queue checks cover captured targets/options, preserved staged images, duplicate clicks, failures and clearing waiting work. Library-tool tests cover artist inference, mixed compilations and band names, nine audio-format artist edits with decoded-audio and artwork preservation, exact Undo, shared queue behavior, eligible-album filters, confirmed file/audio/album copies, partial albums, possible matches, cancellation and read-only duplicate scans. Updater checks additionally verify release versions and checksums, rejected download redirects, cancellation, rollback, a real separate-process EXE replacement/restart, the reopened music-folder path, and scan/queue/staged-cover interlocks. A separate live GitHub check downloads the latest published EXE and verifies it without installing it. A separate live-service check reports source availability and downloads a real high-resolution cover; public-site outages do not fail the deterministic integration suite.

Tests verify audio decoding hashes before/after edits, existing metadata, exact backup restoration, partial albums, corrupt files, stale scans, read-only files, folder images, preserved back covers, cancellation, GUI tab behavior and cover previews. CI additionally opens the isolated EXE with no adjacent DLLs to check the portable dependency loader. Test audio fixtures are short synthesized tones, generated by `tests/generate_fixtures.py`; they contain no copyrighted music. FFmpeg and Python are test tools only and are not included in the app.

Push a `v*` tag, or include `[release]` in a commit message on `main`, to build, test and publish a downloadable release. For a main-branch release, the version comes from the app's project file. A release is published only after the Windows checks pass; existing releases are never replaced automatically.

## License

The app is MIT licensed. The embedded TagLibSharp library is LGPL-2.1-or-later; see `THIRD-PARTY-NOTICES.txt` and `licenses/`. A compatible external `TagLibSharp.dll` next to the EXE takes precedence over the embedded copy, so the library can be replaced independently. Its upstream source and this app's build instructions are available for rebuilding.
