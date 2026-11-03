using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TagLib;
using TagLib.Id3v2;

namespace AlbumArtTool.Management
{
    // Standardizes ID3 tags to version 2.3 with UTF-16 text, the most widely compatible combination
    // (older players and Windows read 2.3; UTF-16 keeps every non-Latin character).
    internal static class Id3Standard
    {
        /// <summary>Files whose tags this applies to: MP3s always; AIFF and WAV when they already carry an ID3v2 tag.</summary>
        internal static bool Applies(TagLib.File file) =>
            file is TagLib.Mpeg.AudioFile || file.GetTag(TagTypes.Id3v2, false) != null && (file is TagLib.Aiff.File || file is TagLib.Riff.File);

        /// <summary>"ID3v2.4, Latin-1/UTF-8 text" when the file is not yet ID3v2.3 with UTF-16 text; null when it already is (or does not apply).</summary>
        internal static string Describe(TagLib.File file)
        {
            if (!Applies(file)) return null;
            if (!(file.GetTag(TagTypes.Id3v2, false) is TagLib.Id3v2.Tag tag)) return "no ID3v2 tag";
            var encodings = tag.Select(Encoding).Where(e => e.HasValue && e.Value != StringType.UTF16).Select(e => Name(e.Value)).Distinct().ToArray();
            if (tag.Version == 3 && encodings.Length == 0) return null;
            return "ID3v2." + tag.Version + (encodings.Length > 0 ? ", " + string.Join("/", encodings) + " text" : "");
        }

        /// <summary>Converts the in-memory tag; the caller saves and verifies.</summary>
        internal static void Apply(TagLib.File file)
        {
            if (!Applies(file)) return;
            bool existed = file.GetTag(TagTypes.Id3v2, false) != null;
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
            tag.Version = 3;
            DowngradeDates(tag);
            foreach (var frame in tag.ToArray()) SetUtf16(frame);
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
            TextInformationFrame.Get(tag, ByteVector.FromString(id, StringType.Latin1), StringType.UTF16, true).Text = new[] { value };

        /// <summary>True when the saved file's ID3v2 tag is version 2.3 with only UTF-16 text.</summary>
        internal static bool Verify(TagLib.File file) =>
            !Applies(file) || file.GetTag(TagTypes.Id3v2, false) is TagLib.Id3v2.Tag tag && tag.Version == 3 && tag.All(f => Encoding(f) is null || Encoding(f) == StringType.UTF16);

        private static StringType? Encoding(Frame frame)
        {
            switch (frame)
            {
                case TextInformationFrame text: return text.TextEncoding;
                case CommentsFrame comment: return comment.TextEncoding;
                case UnsynchronisedLyricsFrame lyrics: return lyrics.TextEncoding;
                case SynchronisedLyricsFrame synced: return synced.TextEncoding;
                case AttachedPictureFrame picture: return picture.TextEncoding;
                case GeneralEncapsulatedObjectFrame item: return item.TextEncoding;
                case TermsOfUseFrame terms: return terms.TextEncoding;
                default: return null;
            }
        }

        private static void SetUtf16(Frame frame)
        {
            switch (frame)
            {
                case TextInformationFrame text: text.TextEncoding = StringType.UTF16; break;
                case CommentsFrame comment: comment.TextEncoding = StringType.UTF16; break;
                case UnsynchronisedLyricsFrame lyrics: lyrics.TextEncoding = StringType.UTF16; break;
                case SynchronisedLyricsFrame synced: synced.TextEncoding = StringType.UTF16; break;
                case AttachedPictureFrame picture: picture.TextEncoding = StringType.UTF16; break;
                case GeneralEncapsulatedObjectFrame item: item.TextEncoding = StringType.UTF16; break;
                case TermsOfUseFrame terms: terms.TextEncoding = StringType.UTF16; break;
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
