using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AlbumArtTool.Core;

namespace AlbumArtTool.Management
{
    internal static class MultiDisc
    {
        private static readonly Regex discFolder = new Regex(@"^(?:CD|DISC|DISK)\s*[-_. ]*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex discSuffix = new Regex(@"(?:\s+[-,:]?\s*|[-,:]\s*|\s*[\[(]\s*)(?:CD|DISC|DISK)\s*[-_. ]*(\d+)(?:\s*(?:OF|/)\s*\d+)?\s*[\])]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private sealed class Part
        {
            internal Track Track; internal string Title, Artist, Scope; internal uint Disc;
        }
        internal static bool IsDiscFolder(string name) => discFolder.IsMatch(name ?? "");
        private static uint Number(Match match) => match.Success && uint.TryParse(match.Groups[1].Value, out uint value) ? value : 0;
        private static Part Identify(Track track, Album album)
        {
            if (string.IsNullOrWhiteSpace(track.AlbumTitle)) return null;
            string folder = Path.GetDirectoryName(track.Path), title = track.AlbumTitle.Trim();
            uint directoryDisc = Number(discFolder.Match(Path.GetFileName(folder))), titleDisc = Number(discSuffix.Match(title));
            if (directoryDisc > 0 && titleDisc > 0 && directoryDisc != titleDisc) return null;
            if (titleDisc > 0) title = discSuffix.Replace(title, "").Trim();
            string scope = directoryDisc > 0 ? Path.GetDirectoryName(folder) : folder;
            uint folderSuffix = Number(discSuffix.Match(Path.GetFileName(folder)));
            if (folderSuffix > 0) scope = Path.Combine(Path.GetDirectoryName(folder), LibraryPaths.Component(title));
            if (IsDiscFolder(title) && directoryDisc > 0) title = Path.GetFileName(scope);
            string artist = !string.IsNullOrWhiteSpace(album.TaggedArtist) ? album.TaggedArtist.Trim() : !string.IsNullOrWhiteSpace(album.MainArtist) ? album.MainArtist : album.Artist;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) || artist.Equals("Unknown artist", StringComparison.OrdinalIgnoreCase)) return null;
            uint disc = directoryDisc > 0 ? directoryDisc : titleDisc > 0 ? titleDisc : folderSuffix > 0 ? folderSuffix : track.Disc;
            if (disc == 0) disc = Number(Regex.Match(Path.GetFileName(track.Path), @"^(\d{1,3})\s*[-_.]\s*\d{1,3}(?:\s|[-_.])"));
            return new Part { Track = track, Title = title, Artist = artist, Scope = scope, Disc = disc };
        }
        internal static void Plan(LibraryPlan plan, ScanResult scan, Dictionary<string, LibraryAction> actions, CancellationToken token)
        {
            var parts = scan.Albums.SelectMany(a => a.Tracks.Select(t => Identify(t, a))).Where(p => p != null);
            foreach (var group in parts.GroupBy(p => p.Scope + "\u001f" + ArtistRules.Normalize(p.Artist) + "\u001f" + ArtistRules.Normalize(p.Title), StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested(); var all = group.ToArray();
                if (all.Where(p => p.Disc > 0).Select(p => p.Disc).Distinct().Count() < 2 && !(all.Any(p => p.Disc > 1) && all.Any(p => p.Disc == 0))) continue;
                if (all.Any(p => !actions.ContainsKey(p.Track.Path))) { plan.Issues.Add(all[0].Title + ": disc merge skipped because not every track could be planned."); continue; }
                var live = all.Where(p => !actions[p.Track.Path].Archive).ToArray();
                if (live.Any(p => p.Disc == 0))
                {
                    if (live.Any(p => p.Disc == 1) || live.Where(p => p.Disc == 0).Any(p => !Path.GetDirectoryName(p.Track.Path).Equals(p.Scope, StringComparison.OrdinalIgnoreCase)))
                    { plan.Issues.Add(all[0].Title + ": disc merge skipped because some disc numbers are ambiguous."); continue; }
                    foreach (var item in live.Where(p => p.Disc == 0)) item.Disc = 1;
                }
                if (live.Select(p => p.Disc).Distinct().Count() < 2) continue;
                if (live.GroupBy(p => p.Disc).Any(g => g.Where(p => p.Track.Number > 0).GroupBy(p => p.Track.Number).Any(numbers => numbers.Count() > 1)))
                { plan.Issues.Add(all[0].Title + ": disc merge skipped because a disc has duplicate track numbers. Review duplicate copies first."); continue; }
                var ordered = live.OrderBy(p => p.Disc).ThenBy(p => p.Track.Number == 0 ? uint.MaxValue : p.Track.Number)
                    .ThenBy(p => Regex.Replace(Path.GetFileName(p.Track.Path), @"\d+", m => m.Value.PadLeft(12, '0')), StringComparer.OrdinalIgnoreCase).ToArray();
                var first = ordered[0];
                string targetFolder = plan.Options.Organize ? Path.Combine(plan.Options.Root, LibraryPaths.Component(first.Artist), LibraryPaths.Component(first.Title)) : first.Scope;
                int digits = Math.Max(2, ordered.Length.ToString().Length);
                var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    for (int i = 0; i < ordered.Length; i++)
                    {
                        var part = ordered[i]; var row = actions[part.Track.Path]; string filename;
                        if (plan.Options.RenameFromTitle) filename = Path.GetFileName(row.Target);
                        else
                        {
                            string name = row.NewTitle ?? (part.Track.HasTitleTag ? part.Track.Title : Path.GetFileNameWithoutExtension(part.Track.Path));
                            if (!part.Track.HasTitleTag) name = Regex.Replace(name, @"^(?:\d{1,3}[-_.])?\d{1,3}\s*[-_.]\s*", "");
                            filename = (i + 1).ToString("D" + digits) + " - " + LibraryPaths.Component(name) + Path.GetExtension(row.Target);
                        }
                        string target = Path.Combine(targetFolder, filename); LibraryPaths.Safe(target, plan.Options.Root);
                        if (target.Length > 250) throw new IOException("The combined album's path would exceed 250 characters.");
                        targets.Add(row.Source, target);
                    }
                }
                catch (Exception e) when (Scanner.IsFileError(e)) { plan.Issues.Add(first.Title + ": " + e.Message); continue; }
                for (int i = 0; i < ordered.Length; i++)
                {
                    var part = ordered[i]; var row = actions[part.Track.Path]; row.Target = targets[row.Source];
                    row.MergeDiscs = true; row.MergedAlbum = first.Title; row.MergedArtist = first.Artist; row.MergedTrack = (uint)(i + 1); row.MergedTrackCount = (uint)ordered.Length;
                    row.MergeGroup = group.Key; row.MergeSourceRoot = first.Scope;
                    row.Reason = (row.Reason == null ? "" : row.Reason + " ") + "Disc " + part.Disc + ", track " + (part.Track.Number > 0 ? part.Track.Number.ToString() : "unnumbered") + " → track " + (i + 1) + "/" + ordered.Length + "; one album, disc 1/1.";
                }
                foreach (var archived in all.Where(p => actions[p.Track.Path].Archive)) actions[archived.Track.Path].MergeGroup = group.Key;
            }
        }
        internal static void AddAssets(LibraryPlan plan, ScanResult scan, Dictionary<string, LibraryAction> actions, HashSet<string> reserved, CancellationToken token)
        {
            var types = new HashSet<string>(new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".pdf", ".txt", ".nfo", ".cue", ".log", ".m3u", ".m3u8", ".pls", ".lrc", ".srt" }, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(actions.Keys.Concat(plan.Actions.Select(a => a.Source)), StringComparer.OrdinalIgnoreCase);
            var tracks = scan.Albums.SelectMany(a => a.Tracks).ToArray();
            foreach (var group in actions.Values.Where(a => a.MergeDiscs).GroupBy(a => a.MergeGroup))
            {
                var first = group.First(); string destination = Path.GetDirectoryName(first.Target);
                var sources = group.Select(a => Path.GetDirectoryName(a.Source)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (Directory.Exists(first.MergeSourceRoot) && !first.MergeSourceRoot.Equals(plan.Options.Root, StringComparison.OrdinalIgnoreCase)) sources.Add(first.MergeSourceRoot);
                foreach (string source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (tracks.Where(t => BackupSettings.Within(t.Path, source)).Any(t => !actions.TryGetValue(t.Path, out var row) || row.MergeGroup != first.MergeGroup)) continue;
                    foreach (string path in LibraryPlanner.WalkFiles(source, token, issues: plan.Issues))
                    {
                        if (!types.Contains(Path.GetExtension(path)) || seen.Contains(path)) continue;
                        string relative = path.Substring(source.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
                        var segments = relative.Split(Path.DirectorySeparatorChar);
                        if (segments.Length > 1 && IsDiscFolder(segments[0])) relative = Path.Combine(segments.Skip(1).ToArray());
                        string target = Path.Combine(destination, relative); if (target.Equals(path, StringComparison.OrdinalIgnoreCase)) { seen.Add(path); continue; }
                        LibraryPaths.Safe(target, plan.Options.Root);
                        if (target.Length > 250) { plan.Issues.Add(path + ": merged sidecar path is too long."); continue; }
                        target = LibraryPaths.Available(target, path, reserved); seen.Add(path);
                        plan.Actions.Add(new LibraryAction { Source = path, Target = target, Stamp = FileStamp.Read(path), MergeGroup = first.MergeGroup, Reason = "Artwork/booklet/sidecar kept with the combined album." });
                    }
                }
            }
        }
    }
}
