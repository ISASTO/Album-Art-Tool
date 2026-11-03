using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AlbumArtTool.Management
{
    // TagLibSharp keeps a file's original size when a tag gets smaller, backfilling the freed
    // space with padding. That is ideal for quick edits but defeats shrinking artwork, so after
    // a shrink the tag is written at its real size. Audio data is never touched.
    internal static class TagCompaction
    {
        private const int FlacPadding = 4096;

        /// <summary>Replaces an ID3v2 tag with a fresh copy of the same frames and version, so it is
        /// rendered without the old tag's size as padding (MP3, AIFF, WAV).</summary>
        internal static void RebuildId3v2(TagLib.File file)
        {
            if (!(file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag old)) return;
            byte version = old.Version;
            var frames = old.Select(frame => frame.Clone()).ToList();
            file.RemoveTags(TagLib.TagTypes.Id3v2);
            if (!(file.GetTag(TagLib.TagTypes.Id3v2, true) is TagLib.Id3v2.Tag fresh)) throw new IOException("The tag could not be rewritten. Original kept.");
            fresh.Version = version;
            foreach (var frame in frames) fresh.AddFrame(frame);
        }

        /// <summary>Drops oversized FLAC PADDING metadata blocks, keeping a small one for future edits.</summary>
        internal static void RemoveFlacPadding(string path)
        {
            if (!path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)) return;
            var blocks = new List<(byte Type, byte[] Data)>();
            long audioStart, padding = 0;
            using (var input = File.OpenRead(path))
            {
                var marker = new byte[4];
                if (input.Read(marker, 0, 4) != 4 || marker[0] != 'f' || marker[1] != 'L' || marker[2] != 'a' || marker[3] != 'C') return;
                while (true)
                {
                    var header = new byte[4];
                    if (input.Read(header, 0, 4) != 4) throw new InvalidDataException("The FLAC metadata is incomplete. Original kept.");
                    int length = header[1] << 16 | header[2] << 8 | header[3];
                    byte type = (byte)(header[0] & 127);
                    if (input.Position + length > input.Length) throw new InvalidDataException("The FLAC metadata is damaged. Original kept.");
                    if (type == 1) { padding += length; input.Seek(length, SeekOrigin.Current); }
                    else
                    {
                        var data = new byte[length]; int read = 0;
                        while (read < length) { int n = input.Read(data, read, length - read); if (n == 0) throw new EndOfStreamException(); read += n; }
                        blocks.Add((type, data));
                    }
                    if ((header[0] & 128) != 0) break;
                }
                audioStart = input.Position;
                if (padding <= FlacPadding * 2 || blocks.Count == 0 || blocks[0].Type != 0) return;
                string temp = path + ".compact";
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                {
                    output.Write(new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C' }, 0, 4);
                    foreach (var block in blocks) WriteBlock(output, block.Type, block.Data, false);
                    WriteBlock(output, 1, new byte[FlacPadding], true);
                    input.Position = audioStart;
                    input.CopyTo(output, 1 << 20);
                }
            }
            File.Move(path + ".compact", path, true);
        }

        private static void WriteBlock(Stream output, byte type, byte[] data, bool last)
        {
            output.WriteByte((byte)(type | (last ? 128 : 0)));
            output.WriteByte((byte)(data.Length >> 16)); output.WriteByte((byte)(data.Length >> 8)); output.WriteByte((byte)data.Length);
            output.Write(data, 0, data.Length);
        }
    }
}
