using System;
using System.Drawing;

namespace AlbumArtTool.Online
{
    internal static class ImageHeader
    {
        // Read dimensions from a bounded prefix; never decode or allocate the full-size image here.
        internal static Size ReadSize(byte[] data)
        {
            if (data == null) return Size.Empty;
            if (data.Length >= 24 && data[0] == 137 && data[1] == 80 && data[2] == 78 && data[3] == 71 &&
                data[4] == 13 && data[5] == 10 && data[6] == 26 && data[7] == 10 &&
                data[12] == 73 && data[13] == 72 && data[14] == 68 && data[15] == 82)
                return Valid(Big32(data, 16), Big32(data, 20));
            if (data.Length >= 10 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8' &&
                (data[4] == '7' || data[4] == '9') && data[5] == 'a')
                return Valid(data[6] | data[7] << 8, data[8] | data[9] << 8);
            if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8) return Size.Empty;
            int offset = 2;
            while (offset < data.Length)
            {
                if (data[offset++] != 0xff) return Size.Empty;
                while (offset < data.Length && data[offset] == 0xff) offset++;
                if (offset >= data.Length) return Size.Empty;
                byte marker = data[offset++];
                if (marker == 0xd9 || marker == 0xda) return Size.Empty;
                if (marker == 0x01 || (marker >= 0xd0 && marker <= 0xd8)) continue;
                if (offset + 2 > data.Length) return Size.Empty;
                int length = data[offset] << 8 | data[offset + 1];
                if (length < 2) return Size.Empty;
                bool frame = marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc;
                if (frame)
                {
                    if (length < 8 || offset + 7 > data.Length) return Size.Empty;
                    return Valid(data[offset + 5] << 8 | data[offset + 6], data[offset + 3] << 8 | data[offset + 4]);
                }
                if (length > data.Length - offset) return Size.Empty;
                offset += length;
            }
            return Size.Empty;
        }
        private static int Big32(byte[] data, int offset) => data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3];
        private static Size Valid(int width, int height) => width > 0 && height > 0 ? new Size(width, height) : Size.Empty;
    }
}
