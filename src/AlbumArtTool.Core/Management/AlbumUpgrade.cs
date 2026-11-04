using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AlbumArtTool.Core;
using AlbumArtTool.Online;

namespace AlbumArtTool.Management
{
    /// <summary>VideoOnly: YouTube Music has only the music video of this song, which is never downloaded.</summary>
    public enum PairQuality { Exact, Close, Different, Missing, VideoOnly }

    /// <summary>One of your songs and the YouTube Music track that would replace it.</summary>
    public sealed class TrackPairing
    {
        public Track Local { get; set; }
        public YtmTrack Remote { get; set; }
        public int LocalKbps { get; set; }
        public bool Low { get; set; }
        public double TitleScore { get; set; }
        public double SecondsApart { get; set; }
        public PairQuality Quality { get; set; }
        public bool Selected { get; set; }
        /// <summary>Replaced in this session (the new MP3 is in place).</summary>
        public bool Replaced { get; set; }
        public bool CanReplace => Remote != null && !Replaced && Quality != PairQuality.VideoOnly;
    }

    public static partial class AlbumUpgrade
    {
        /// <summary>Length difference (seconds) that still counts as the same recording, and the most a "close" match may differ.</summary>
        public const double ExactSeconds = 3, CloseSeconds = 8;

        [GeneratedRegex(@"\s*[\(\[\{][^\)\]\}]*\b(deluxe|expanded|edition|remaster(ed)?|anniversary|bonus|version|explicit|clean)\b[^\)\]\}]*[\)\]\}]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex EditionLabel();
        [GeneratedRegex(@"\s*-\s*(ep|single)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex KindSuffix();
        [GeneratedRegex(@"\s+")]
        private static partial Regex Spaces();

        public static string Query(string artist, string album) => (Clean(artist) + " " + AlbumTitle(album)).Trim();

        /// <summary>Album titles without edition labels, so "Donda (Deluxe)" and "Donda" compare equal.</summary>
        public static string AlbumTitle(string title) => KindSuffix().Replace(EditionLabel().Replace(title ?? "", " "), "").Trim();

        /// <summary>0–100: how well a YouTube Music album matches the local album's artist and title (and track count once known).</summary>
        public static int AlbumScore(string artist, string album, int localTracks, YtmAlbum candidate)
        {
            double title = Similarity(AlbumTitle(album), AlbumTitle(candidate.Title));
            double who = string.IsNullOrWhiteSpace(artist) ? 0.6 : Math.Max(Similarity(artist, candidate.Artist), Contains(candidate.Artist, artist) ? 0.9 : 0);
            double score = title * 65 + who * 30;
            if (candidate.TrackCount > 0 && localTracks > 0) score += 5 * Math.Max(0, 1 - Math.Abs(candidate.TrackCount - localTracks) / (double)Math.Max(localTracks, 1));
            else score += 2.5;
            return (int)Math.Round(Math.Min(100, score));
        }

        /// <summary>
        /// Pairs every local song with at most one YouTube Music track (title first, then track number and length).
        /// Low songs with an exact match (same title, length within 3 s) are selected; anything else waits for the user.
        /// </summary>
        public static List<TrackPairing> Pair(IReadOnlyList<Track> local, ISet<string> lowPaths, YtmAlbum album, Func<Track, int> kbps, Func<Track, TimeSpan> length = null)
        {
            length = length ?? (t => t.Duration);
            var remote = album?.Tracks ?? new List<YtmTrack>();
            var options = new List<(int L, int R, double Score, double Title, double Apart)>();
            for (int l = 0; l < local.Count; l++)
                for (int r = 0; r < remote.Count; r++)
                {
                    double title = TitleSimilarity(local[l].HasTitleTag ? local[l].Title : System.IO.Path.GetFileNameWithoutExtension(local[l].Path), remote[r].Title);
                    var mine = length(local[l]);
                    double apart = mine > TimeSpan.Zero && remote[r].Duration > TimeSpan.Zero ? Math.Abs((mine - remote[r].Duration).TotalSeconds) : 999;
                    bool sameNumber = local[l].Number > 0 && local[l].Number == remote[r].Index;
                    if (title < 0.6 && !(sameNumber && title >= 0.35 && apart <= ExactSeconds)) continue;
                    double score = title + (sameNumber ? 0.15 : 0) + (apart <= ExactSeconds ? 0.2 : apart <= CloseSeconds ? 0.1 : 0);
                    options.Add((l, r, score, title, apart));
                }
            var pairings = local.Select(t => new TrackPairing { Local = t, Low = lowPaths.Contains(t.Path), LocalKbps = kbps(t), Quality = PairQuality.Missing }).ToList();
            var usedRemote = new HashSet<int>();
            foreach (var option in options.OrderByDescending(o => o.Score))
            {
                var pairing = pairings[option.L];
                if (pairing.Remote != null || usedRemote.Contains(option.R)) continue;
                usedRemote.Add(option.R);
                pairing.Remote = remote[option.R]; pairing.TitleScore = option.Title; pairing.SecondsApart = option.Apart;
                // Before ResolveAudioAsync runs, a video entry still counts for choosing the album; Plan never downloads one.
                pairing.Quality = remote[option.R].VideoOnly ? PairQuality.VideoOnly :
                    option.Apart <= ExactSeconds && option.Title >= 0.85 ? PairQuality.Exact : option.Apart <= CloseSeconds ? PairQuality.Close : PairQuality.Different;
            }
            foreach (var pairing in pairings) pairing.Selected = pairing.Low && pairing.Quality == PairQuality.Exact;
            return pairings;
        }

        /// <summary>
        /// The library plan for the selected pairings: each song is replaced in place by an MP3 320 of the same name
        /// (a non-MP3 original becomes .mp3). Pairings that would overwrite a different existing file are reported instead.
        /// </summary>
        internal static LibraryPlan Plan(IEnumerable<TrackPairing> pairings, LibraryOptions options)
        {
            options.Root = System.IO.Path.GetFullPath(options.Root); options.BackupRoot = System.IO.Path.GetFullPath(options.BackupRoot);
            if (BackupSettings.Within(options.BackupRoot, options.Root)) throw new System.IO.IOException("The backup folder must be outside the music folder so your player cannot index the backups.");
            var plan = new LibraryPlan { Options = options };
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pairing in pairings.Where(p => p.Selected && p.Remote != null))
            {
                if (pairing.Remote.MusicVideo || pairing.Remote.VideoOnly)
                { plan.Issues.Add(pairing.Local.Path + ": YouTube Music only lists the music video of this song; music videos are never downloaded."); continue; }
                var track = pairing.Local;
                string target = System.IO.Path.ChangeExtension(track.Path, ".mp3");
                if (!target.Equals(track.Path, StringComparison.OrdinalIgnoreCase) && (System.IO.File.Exists(target) || !targets.Add(target)))
                { plan.Issues.Add(track.Path + ": " + System.IO.Path.GetFileName(target) + " already exists; this song was not replaced."); continue; }
                targets.Add(target);
                plan.Actions.Add(new LibraryAction
                {
                    Source = track.Path, Target = target, Track = track, Stamp = track.Stamp, Selected = true,
                    ReplacementUrl = pairing.Remote.Url, ReplacementTitle = pairing.Remote.Title, ReplacementSeconds = pairing.Remote.Duration.TotalSeconds,
                    Reason = (pairing.LocalKbps > 0 ? pairing.LocalKbps + " kbps" : "Low bitrate") + " → YouTube Music \"" + pairing.Remote.Title + "\" → MP3 320."
                });
            }
            return plan;
        }

        /// <summary>
        /// Swaps every music-video entry of the album for the artist's audio upload of the same song, found with YouTube Music's
        /// song search: same title, same artist, and the same album (or, from a single or another release, the same length within 2 s).
        /// Entries with no audio upload are marked VideoOnly and never offered for download.
        /// </summary>
        internal static async System.Threading.Tasks.Task ResolveAudioAsync(IYouTubeMusic music, YtmAlbum album, string artist, System.Threading.CancellationToken token, IProgress<(int Done, int Total)> progress = null)
        {
            if (album.AudioResolved || album.Tracks == null) return;
            var videos = album.Tracks.Where(t => t.MusicVideo).ToList();
            int done = 0;
            foreach (var track in videos)
            {
                token.ThrowIfCancellationRequested();
                string who = string.IsNullOrWhiteSpace(album.Artist) ? artist : album.Artist;
                var songs = await music.SearchSongsAsync((who + " " + TitleCleanup.Clean(track.Title)).Trim(), token).ConfigureAwait(true);
                var audio = songs.Where(song => !song.MusicVideo && TitleSimilarity(song.Title, track.Title) >= 0.9 &&
                        (Similarity(song.Artists, who) >= 0.6 || Contains(song.Artists, who) || Contains(who, song.Artists)) &&
                        (song.AlbumBrowseId == album.BrowseId || track.Duration > TimeSpan.Zero && song.Duration > TimeSpan.Zero && Math.Abs((song.Duration - track.Duration).TotalSeconds) <= 2))
                    .OrderByDescending(song => song.AlbumBrowseId == album.BrowseId)
                    .ThenBy(song => Math.Abs((song.Duration - track.Duration).TotalSeconds)).FirstOrDefault();
                if (audio != null)
                {
                    track.ReplacedVideoId = track.VideoId; track.VideoId = audio.VideoId; track.MusicVideo = false;
                    if (audio.Duration > TimeSpan.Zero) track.Duration = audio.Duration;
                    if (audio.AlbumBrowseId != album.BrowseId) track.AlbumTitle = audio.AlbumTitle;
                }
                else track.VideoOnly = true;
                progress?.Report((++done, videos.Count));
                await System.Threading.Tasks.Task.Delay(120, token).ConfigureAwait(true);
            }
            album.AudioResolved = true;
        }

        /// <summary>Titles compared without featured-artist credits, punctuation, accents or letter case.</summary>
        public static double TitleSimilarity(string first, string second) => Similarity(TitleCleanup.Clean(first ?? ""), TitleCleanup.Clean(second ?? ""));

        public static double Similarity(string first, string second)
        {
            string a = Normalize(first), b = Normalize(second);
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 1;
            var left = new HashSet<string>(a.Split(' ')); var right = new HashSet<string>(b.Split(' '));
            double words = (double)left.Intersect(right).Count() / left.Union(right).Count();
            return Math.Max(words, Characters(a, b));
        }

        private static bool Contains(string haystack, string needle)
        {
            string a = Normalize(haystack), b = Normalize(needle);
            return a.Length > 0 && b.Length > 0 && (" " + a + " ").Contains(" " + b + " ", StringComparison.Ordinal);
        }

        // 1 − edit distance / length: tolerant of small spelling and punctuation differences.
        private static double Characters(string a, string b)
        {
            if (Math.Max(a.Length, b.Length) > 200) return 0;
            var previous = new int[b.Length + 1]; var current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) previous[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                (previous, current) = (current, previous);
            }
            return 1 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
        }

        private static string Clean(string value) => Spaces().Replace(value ?? "", " ").Trim();

        internal static string Normalize(string value)
        {
            var builder = new StringBuilder();
            foreach (char c in (value ?? "").Replace("&", " and ").Normalize(NormalizationForm.FormD).ToLowerInvariant())
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
            return Spaces().Replace(builder.ToString(), " ").Trim();
        }
    }
}
