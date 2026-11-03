using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TagLib;
using TagLib.Id3v2;

namespace AlbumArtTool.Management
{
    // Standardizes ID3 tags to version 2.3, the version every player reads. Text that Latin-1 can represent stays (or becomes)
    // Latin-1; a field with any character outside Latin-1 is written as UTF-16. No character is ever replaced to make text fit.
    internal static class Id3Standard
    {
        /// <summary>Files whose tags this applies to: MP3s always; AIFF and WAV when they already carry an ID3v2 tag.</summary>
        internal static bool Applies(TagLib.File file) =>
            file is TagLib.Mpeg.AudioFile || OnDisk(file) && (file is TagLib.Aiff.File || file is TagLib.Riff.File);

        // TagLib adds an empty in-memory ID3v2 tag to every MP3 it opens; only a tag stored in the file counts.
        private static bool OnDisk(TagLib.File file) => (file.TagTypesOnDisk & TagTypes.Id3v2) != 0;

        /// <summary>"ID3v2.4, UTF-8 text" when the file's tag is not yet in the standard form; null when it already is (or does not apply).</summary>
        internal static string Describe(TagLib.File file)
        {
            if (!Applies(file)) return null;
            if (!OnDisk(file) || !(file.GetTag(TagTypes.Id3v2, false) is TagLib.Id3v2.Tag tag)) return "no ID3v2 tag";
            var encodings = tag.Where(f => Encoding(f) is StringType e && e != Target(f, e)).Select(f => Name(Encoding(f).Value)).Distinct().ToArray();
            int duplicates = Duplicates(tag).Count;
            bool ape = HasApe(file);
            if (tag.Version == 3 && encodings.Length == 0 && duplicates == 0 && !ape) return null;
            return "ID3v2." + tag.Version + (encodings.Length > 0 ? ", " + string.Join("/", encodings) + " text" : "") +
                (duplicates > 0 ? ", " + duplicates + " duplicate frame" + (duplicates == 1 ? "" : "s") : "") + (ape ? ", extra APE tag" : "");
        }

        /// <summary>Converts the in-memory tag and returns a snapshot of its text, which Verify compares after saving.</summary>
        internal static List<string> Apply(TagLib.File file)
        {
            if (!Applies(file)) return null;
            bool existed = OnDisk(file);
            // Collect what other tag types hold before creating a new ID3v2 tag, so nothing is lost.
            var others = new List<TagLib.Tag>();
            if (!existed)
                foreach (var type in new[] { TagTypes.Ape, TagTypes.Id3v1, TagTypes.RiffInfo })
                {
                    try { var other = file.GetTag(type, false); if (other != null) others.Add(other); }
                    catch (NotSupportedException) { } catch (NotImplementedException) { }
                }
            if (!(file.GetTag(TagTypes.Id3v2, true) is TagLib.Id3v2.Tag tag)) throw new IOException("An ID3v2 tag could not be created. Original kept.");
            foreach (var other in others) other.CopyTo(tag, false);
            foreach (var frame in Duplicates(tag)) tag.RemoveFrame(frame);
            // An APE tag on an MP3 is non-standard (and often holds a second copy of the cover): fill any gaps from it, then drop it.
            if (HasApe(file))
            {
                file.GetTag(TagTypes.Ape, false)?.CopyTo(tag, false);
                file.RemoveTags(TagTypes.Ape);
            }
            tag.Version = 3;
            DowngradeDates(tag);
            // Reading the encoding first matters: TagLib re-reads an unparsed frame when saving and would discard the change.
            foreach (var frame in tag.ToArray()) if (Encoding(frame) is StringType current) SetEncoding(frame, Target(frame, current));
            return Snapshot(tag);
        }

        /// <summary>
        /// The ID3v2.3 encoding for a frame: Latin-1 stays Latin-1 and UTF-16 stays UTF-16 while the text fits;
        /// encodings ID3v2.3 lacks (UTF-8, UTF-16BE) become Latin-1 when every character fits, otherwise UTF-16.
        /// </summary>
        private static StringType Target(Frame frame, StringType current)
        {
            bool fits = Strings(frame).All(FitsLatin1);
            if (current == StringType.UTF16) return StringType.UTF16;
            return fits ? StringType.Latin1 : StringType.UTF16;
        }

        // Latin-1 holds exactly U+0000–U+00FF; anything else would be lost or turned into "?".
        private static bool FitsLatin1(string text) => text == null || text.All(c => c <= '\u00ff');

        /// <summary>Every text value a frame stores (descriptions included).</summary>
        private static IEnumerable<string> Strings(Frame frame)
        {
            switch (frame)
            {
                case UserTextInformationFrame user: return new[] { user.Description }.Concat(user.Text);
                case TextInformationFrame text: return text.Text;
                case CommentsFrame comment: return new[] { comment.Description, comment.Text };
                case UnsynchronisedLyricsFrame lyrics: return new[] { lyrics.Description, lyrics.Text };
                case SynchronisedLyricsFrame synced: return new[] { synced.Description }.Concat(synced.Text.Select(t => t.Text));
                case AttachmentFrame attachment: return new[] { attachment.Description, attachment.Filename };
                case TermsOfUseFrame terms: return new[] { terms.Text };
                default: return Array.Empty<string>();
            }
        }

        // Dates and genres are re-expressed by TagLib between ID3 versions, so the snapshot compares the remaining text.
        private static readonly HashSet<string> Reformatted = new HashSet<string> { "TYER", "TDAT", "TIME", "TORY", "TDRC", "TDOR", "TRDA", "TCON" };

        private static List<string> Snapshot(TagLib.Id3v2.Tag tag) =>
            tag.Where(f => Encoding(f).HasValue && !Reformatted.Contains(f.FrameId.ToString()))
               .Select(f => string.Join("\u001e", Strings(f).Select(t => t ?? ""))).OrderBy(t => t, StringComparer.Ordinal).ToList();

        private static bool HasApe(TagLib.File file) => file is TagLib.Mpeg.AudioFile && (file.TagTypesOnDisk & TagTypes.Ape) != 0 && file.GetTag(TagTypes.Ape, false) != null;

        /// <summary>
        /// Frames that repeat one that may appear only once (the same text frame, TXXX/COMM/USLT description, or an identical picture).
        /// Earlier versions of this tool could leave such repeats, copied from an ID3v1/APE tag, in front of the real frame,
        /// so the last of each group is the one kept.
        /// </summary>
        internal static List<Frame> Duplicates(TagLib.Id3v2.Tag tag)
        {
            string Key(Frame frame)
            {
                switch (frame)
                {
                    case UserTextInformationFrame user: return "TXXX\u001f" + (user.Description ?? "").ToUpperInvariant();
                    case TextInformationFrame text: return text.FrameId.ToString();
                    case CommentsFrame comment: return "COMM\u001f" + (comment.Description ?? "") + "\u001f" + (comment.Language ?? "");
                    case UnsynchronisedLyricsFrame lyrics: return "USLT\u001f" + (lyrics.Description ?? "") + "\u001f" + (lyrics.Language ?? "");
                    case AttachmentFrame picture: return picture.FrameId.ToString() + "\u001f" + (int)picture.Type + "\u001f" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(picture.Data?.Data ?? Array.Empty<byte>()));
                    default: return null;
                }
            }
            var frames = tag.ToArray();
            var last = new Dictionary<string, Frame>();
            foreach (var frame in frames) { string key = Key(frame); if (key != null) last[key] = frame; }
            return frames.Where(f => Key(f) is string key && !ReferenceEquals(last[key], f)).ToList();
        }

        // ID3v2.4 keeps dates in TDRC/TDOR ("2024-05-17T10:30"); ID3v2.3 players read TYER, TDAT (DDMM), TIME (HHMM) and TORY.
        private static void DowngradeDates(TagLib.Id3v2.Tag tag)
        {
            foreach (var frame in tag.GetFrames<TextInformationFrame>(ByteVector.FromString("TDRC", StringType.Latin1)).ToArray())
            {
                string text = (frame.Text.FirstOrDefault() ?? "").Trim();
                tag.RemoveFrame(frame);
                if (text.Length >= 4 && text.Take(4).All(char.IsDigit)) Set(tag, "TYER", text.Substring(0, 4));
                if (text.Length >= 10 && text[4] == '-' && text[7] == '-') Set(tag, "TDAT", text.Substring(8, 2) + text.Substring(5, 2));
                if (text.Length >= 16 && text[10] == 'T' && text[13] == ':') Set(tag, "TIME", text.Substring(11, 2) + text.Substring(14, 2));
            }
            foreach (var frame in tag.GetFrames<TextInformationFrame>(ByteVector.FromString("TDOR", StringType.Latin1)).ToArray())
            {
                string text = (frame.Text.FirstOrDefault() ?? "").Trim();
                tag.RemoveFrame(frame);
                if (text.Length >= 4 && text.Take(4).All(char.IsDigit)) Set(tag, "TORY", text.Substring(0, 4));
            }
        }

        private static void Set(TagLib.Id3v2.Tag tag, string id, string value) =>
            TextInformationFrame.Get(tag, ByteVector.FromString(id, StringType.Latin1), StringType.Latin1, true).Text = new[] { value };

        /// <summary>True when the saved file's tag is ID3v2.3 in Latin-1/UTF-16 as chosen above and every character survived unchanged.</summary>
        internal static bool Verify(TagLib.File file, List<string> snapshot = null) => Problem(file, snapshot) == null;

        /// <summary>Why the saved tag is not in the standard form, or null when it is.</summary>
        internal static string Problem(TagLib.File file, List<string> snapshot = null)
        {
            if (!Applies(file)) return null;
            if (!(file.GetTag(TagTypes.Id3v2, false) is TagLib.Id3v2.Tag tag)) return "no ID3v2 tag was written";
            if (tag.Version != 3) return "the tag is ID3v2." + tag.Version;
            var wrong = tag.Where(f => Encoding(f) is StringType e && (e != StringType.Latin1 && e != StringType.UTF16 || e == StringType.Latin1 && !Strings(f).All(FitsLatin1)))
                .Select(f => f.FrameId.ToString()).Distinct().ToArray();
            if (wrong.Length > 0) return string.Join(", ", wrong) + " text is not in Latin-1/UTF-16";
            if (Duplicates(tag).Count > 0) return "duplicate frames remain";
            if (HasApe(file)) return "the APE tag remains";
            if (snapshot != null && !Snapshot(tag).SequenceEqual(snapshot, StringComparer.Ordinal)) return "some text did not survive unchanged";
            return null;
        }

        private static StringType? Encoding(Frame frame)
        {
            switch (frame)
            {
                case TextInformationFrame text: return text.TextEncoding;
                case CommentsFrame comment: return comment.TextEncoding;
                case UnsynchronisedLyricsFrame lyrics: return lyrics.TextEncoding;
                case SynchronisedLyricsFrame synced: return synced.TextEncoding;
                // Pictures (APIC) and objects (GEOB) read from a file are AttachmentFrames.
                case AttachmentFrame attachment: return attachment.TextEncoding;
                case TermsOfUseFrame terms: return terms.TextEncoding;
                default: return null;
            }
        }

        private static void SetEncoding(Frame frame, StringType encoding)
        {
            switch (frame)
            {
                case TextInformationFrame text: text.TextEncoding = encoding; break;
                case CommentsFrame comment: comment.TextEncoding = encoding; break;
                case UnsynchronisedLyricsFrame lyrics: lyrics.TextEncoding = encoding; break;
                case SynchronisedLyricsFrame synced: synced.TextEncoding = encoding; break;
                case AttachmentFrame attachment: attachment.TextEncoding = encoding; break;
                case TermsOfUseFrame terms: terms.TextEncoding = encoding; break;
            }
        }

        private static string Name(StringType type)
        {
            switch (type)
            {
                case StringType.Latin1: return "Latin-1";
                case StringType.UTF8: return "UTF-8";
                case StringType.UTF16BE: return "UTF-16BE";
                default: return type.ToString();
            }
        }
    }
}
