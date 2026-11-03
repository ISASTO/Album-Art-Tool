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

        /// <summary>Removes padding that tag writers leave behind, per container.</summary>
        internal static void Compact(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (extension == ".flac") RemoveFlacPadding(path);
                else if (extension == ".wav") RemoveRiffJunk(path);
                else if (extension == ".m4a" || extension == ".m4b" || extension == ".mp4" || extension == ".aac") RemoveMp4Free(path);
            }
            finally { try { File.Delete(path + ".compact"); } catch { } }
        }

        // RIFF/WAVE: drop JUNK/PAD chunks. Nothing in a WAV file refers to absolute offsets.
        internal static void RemoveRiffJunk(string path)
        {
            byte[] head = new byte[12];
            using (var input = File.OpenRead(path))
            {
                if (input.Read(head, 0, 12) != 12 || head[0] != 'R' || head[1] != 'I' || head[2] != 'F' || head[3] != 'F') return;
                var keep = new List<(long Offset, long Length)>(); long removed = 0;
                while (input.Position + 8 <= input.Length)
                {
                    long start = input.Position; var header = new byte[8];
                    input.Read(header, 0, 8);
                    string id = System.Text.Encoding.ASCII.GetString(header, 0, 4);
                    long size = BitConverter.ToUInt32(header, 4), total = 8 + size + (size & 1);
                    if (start + total > input.Length) total = input.Length - start;
                    if ((id == "JUNK" || id == "junk" || id == "PAD ") && size > 64) removed += total; else keep.Add((start, total));
                    input.Position = start + total;
                }
                if (removed < 8192) return;
                using (var output = new FileStream(path + ".compact", FileMode.CreateNew, FileAccess.Write))
                {
                    long body = 4 + keep.Sum(k => k.Length);
                    output.Write(head, 0, 4); output.Write(BitConverter.GetBytes((uint)body), 0, 4); output.Write(head, 8, 4);
                    foreach (var (offset, length) in keep) Copy(input, output, offset, length);
                }
            }
            File.Move(path + ".compact", path, true);
        }

        private static readonly HashSet<string> Mp4Containers = new HashSet<string> { "moov", "trak", "mdia", "minf", "stbl", "udta", "edts", "dinf", "ilst", "meta" };

        // MP4/M4A: drop free/skip atoms. Removing bytes in front of the audio moves it, so every
        // chunk offset (stco/co64) is lowered by the same amount. Fragmented files are left alone.
        internal static void RemoveMp4Free(string path)
        {
            var top = new List<(string Type, long Offset, long Size)>();
            using (var input = File.OpenRead(path))
            {
                while (input.Position + 8 <= input.Length)
                {
                    long start = input.Position; var header = new byte[16];
                    input.Read(header, 0, 8);
                    long size = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
                    string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
                    if (size == 1) { input.Read(header, 8, 8); size = (long)ReadUInt64(header, 8); }
                    else if (size == 0) size = input.Length - start;
                    if (size < 8 || start + size > input.Length) return; // unexpected layout: leave the file alone
                    top.Add((type, start, size));
                    input.Position = start + size;
                }
                if (top.Count(b => b.Type == "moov") != 1 || top.Any(b => b.Type == "moof") || !top.Any(b => b.Type == "mdat")) return;
                var moovBox = top.Single(b => b.Type == "moov");
                if (moovBox.Size > 256L * 1024 * 1024) return;
                var moov = new byte[moovBox.Size]; input.Position = moovBox.Offset;
                int read = 0; while (read < moov.Length) { int n = input.Read(moov, read, moov.Length - read); if (n == 0) return; read += n; }
                if (ReadUInt32(moov, 0) == 1) return; // 64-bit moov header: unusual, leave alone
                byte[] rebuilt = Rebuild(moov, 0, moov.Length, out long removedInMoov);
                long firstMdat = top.First(b => b.Type == "mdat").Offset;
                long removedBeforeAudio = top.Where(b => (b.Type == "free" || b.Type == "skip") && b.Offset < firstMdat).Sum(b => b.Size) +
                    (moovBox.Offset < firstMdat ? removedInMoov : 0);
                long removedTotal = removedInMoov + top.Where(b => b.Type == "free" || b.Type == "skip").Sum(b => b.Size);
                if (removedTotal < 8192) return;
                if (removedBeforeAudio > 0 && !ShiftChunkOffsets(rebuilt, 0, rebuilt.Length, removedBeforeAudio)) return;
                using (var output = new FileStream(path + ".compact", FileMode.CreateNew, FileAccess.Write))
                    foreach (var box in top)
                    {
                        if (box.Type == "free" || box.Type == "skip") continue;
                        if (box.Type == "moov") output.Write(rebuilt, 0, rebuilt.Length);
                        else Copy(input, output, box.Offset, box.Size);
                    }
            }
            File.Move(path + ".compact", path, true);
        }

        // Rebuilds one box (and container children) without free/skip descendants.
        private static byte[] Rebuild(byte[] data, int offset, int size, out long removed)
        {
            removed = 0;
            string type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
            if (!Mp4Containers.Contains(type)) return data.AsSpan(offset, size).ToArray();
            int header = 8;
            // "meta" is a full box (version and flags) in iTunes files; QuickTime files use a plain container.
            if (type == "meta" && size >= 12 && ReadUInt32(data, offset + 8) == 0) header = 12;
            var output = new MemoryStream();
            output.Write(data, offset, header);
            int position = offset + header, end = offset + size;
            while (position + 8 <= end)
            {
                long childSize = ReadUInt32(data, position);
                string childType = System.Text.Encoding.ASCII.GetString(data, position + 4, 4);
                if (childSize == 1 || childSize == 0 || childSize < 8 || position + childSize > end)
                {
                    // Copy anything unusual verbatim.
                    output.Write(data, position, end - position); position = end; break;
                }
                if (childType == "free" || childType == "skip") removed += childSize;
                else
                {
                    var child = Rebuild(data, position, (int)childSize, out long inner);
                    removed += inner; output.Write(child, 0, child.Length);
                }
                position += (int)childSize;
            }
            if (position < end) output.Write(data, position, end - position);
            var result = output.ToArray();
            WriteUInt32(result, 0, (uint)result.Length);
            return result;
        }

        private static bool ShiftChunkOffsets(byte[] data, int offset, int size, long delta)
        {
            string type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
            if (type == "stco" || type == "co64")
            {
                if (size < 16) return false;
                long count = ReadUInt32(data, offset + 12); int width = type == "stco" ? 4 : 8;
                if (16 + count * width > size) return false;
                for (long i = 0; i < count; i++)
                {
                    int at = offset + 16 + (int)(i * width);
                    if (width == 4) { long value = ReadUInt32(data, at) - delta; if (value < 0) return false; WriteUInt32(data, at, (uint)value); }
                    else { long value = (long)ReadUInt64(data, at) - delta; if (value < 0) return false; WriteUInt64(data, at, (ulong)value); }
                }
                return true;
            }
            if (!Mp4Containers.Contains(type)) return true;
            int header = type == "meta" && size >= 12 && ReadUInt32(data, offset + 8) == 0 ? 12 : 8;
            int position = offset + header, end = offset + size;
            while (position + 8 <= end)
            {
                long childSize = ReadUInt32(data, position);
                if (childSize < 8 || position + childSize > end) break;
                if (!ShiftChunkOffsets(data, position, (int)childSize, delta)) return false;
                position += (int)childSize;
            }
            return true;
        }

        private static long ReadUInt32(byte[] d, int i) => (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);
        private static ulong ReadUInt64(byte[] d, int i) => (ulong)ReadUInt32(d, i) << 32 | (ulong)ReadUInt32(d, i + 4);
        private static void WriteUInt32(byte[] d, int i, uint v) { d[i] = (byte)(v >> 24); d[i + 1] = (byte)(v >> 16); d[i + 2] = (byte)(v >> 8); d[i + 3] = (byte)v; }
        private static void WriteUInt64(byte[] d, int i, ulong v) { WriteUInt32(d, i, (uint)(v >> 32)); WriteUInt32(d, i + 4, (uint)v); }

        private static void Copy(Stream input, Stream output, long offset, long length)
        {
            input.Position = offset; var buffer = new byte[1 << 20];
            while (length > 0) { int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length)); if (n == 0) throw new EndOfStreamException(); output.Write(buffer, 0, n); length -= n; }
        }

        private static void WriteBlock(Stream output, byte type, byte[] data, bool last)
        {
            output.WriteByte((byte)(type | (last ? 128 : 0)));
            output.WriteByte((byte)(data.Length >> 16)); output.WriteByte((byte)(data.Length >> 8)); output.WriteByte((byte)data.Length);
            output.Write(data, 0, data.Length);
        }
    }
}
