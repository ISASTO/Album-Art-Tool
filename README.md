# Album Art Tool

A small, portable Windows app for finding and fixing missing album covers.

**[Download the Windows app](https://github.com/ISASTO/Album-Art-Tool/releases/latest)**

## Use it

1. Download **AlbumArtTool.exe** from Releases. Put it in the top folder of your music collection and double-click it. You can also keep it elsewhere and use **Choose folder**.
2. The app automatically scans that folder and every subfolder.
3. **Add missing album art** shows albums where one or more tracks lack an embedded front cover. **Change existing album art** shows albums with embedded or folder artwork.
4. Drag a JPG, PNG, BMP or GIF from File Explorer onto an album row or the cover preview. **Choose image** works too.
5. Check the preview and click **Apply cover**. Changes happen only when you click Apply.

No installer, accounts, network requests, music database or background service. Windows 10 (1903 or newer) and Windows 11 use the .NET Framework 4.8 runtime already included with Windows. The EXE embeds its only third-party library, TagLibSharp. The ZIP includes the same EXE and license notices.

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

**Undo last change** restores your most recent edit during the current app session. Backups remain after you close the app; to recover an older edit, close music players and copy the wanted original from its backup folder over the changed file. Backups contain full audio files and use disk space; you can delete them once satisfied with your edits.

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

Tests verify audio decoding hashes before/after edits, existing metadata, exact backup restoration, partial albums, corrupt files, stale scans, read-only files, folder images, preserved back covers, cancellation, GUI tab behavior and cover previews. CI additionally opens the isolated EXE with no adjacent DLLs to check the portable dependency loader. Test audio fixtures are short synthesized tones, generated by `tests/generate_fixtures.py`; they contain no copyrighted music. FFmpeg and Python are test tools only and are not included in the app.

Push a `v*` tag, or include `[release]` in a commit message on `main`, to build, test and publish a downloadable release. For a main-branch release, the version comes from the app's project file. A release is published only after the Windows checks pass; existing releases are never replaced automatically.

## License

The app is MIT licensed. The embedded TagLibSharp library is LGPL-2.1-or-later; see `THIRD-PARTY-NOTICES.txt` and `licenses/`. A compatible external `TagLibSharp.dll` next to the EXE takes precedence over the embedded copy, so the library can be replaced independently. Its upstream source and this app's build instructions are available for rebuilding.
