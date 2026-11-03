using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using TagLib;

namespace AlbumArtTool.Core
{
    public enum ImageKind { Jpeg, Png, Bmp, Gif }

    public static class Artwork
    {
        public const int MaxImageBytes = 30 * 1024 * 1024;
        private const long MaxPixels = 40000000;

        public static bool IsCover(IPicture picture) => picture.Type == PictureType.FrontCover || picture.Type == PictureType.Other;

        public static byte[] GetCover(TagLib.Tag tag) => GetCover(tag, null);

        internal static byte[] GetCover(TagLib.Tag tag, ArtworkValidationCache cache)
        {
            var pictures = tag.Pictures;
            // Front covers first, then unclassified pictures, each in tag order.
            for (int pass = 0; pass < 2; pass++)
                foreach (var picture in pictures)
                {
                    if (pass == 0 ? picture.Type != PictureType.FrontCover : picture.Type != PictureType.Other) continue;
                    byte[] bytes = picture.Data.Data;
                    if (bytes.Length == 0 || bytes.Length > MaxImageBytes) continue;
                    if (cache != null) { if (cache.Valid(bytes)) return bytes; continue; }
                    if (IsValid(bytes)) return bytes;
                }
            return null;
        }

        public static bool IsImageError(Exception e) => e is ArgumentException || e is OutOfMemoryException ||
            e is InvalidDataException || e is System.Runtime.InteropServices.ExternalException;

        // Fully parses the image data at the codec's smallest scale. JPEG covers are decoded at
        // 1/8 size, which reads every byte but costs a fraction of a full-resolution decode.
        internal static bool IsValid(byte[] bytes)
        {
            try { using (Load(bytes, 1).Bitmap) return true; }
            catch (Exception e) when (IsImageError(e)) { return false; }
        }

        /// <summary>Decodes at full resolution with EXIF orientation applied.</summary>
        public static SKBitmap Decode(byte[] bytes) => Load(bytes, 0).Bitmap;

        /// <summary>Oriented dimensions read from the image header only.</summary>
        public static (int Width, int Height) ReadInfo(byte[] bytes)
        {
            using (var codec = OpenCodec(bytes, out var data))
            using (data)
            {
                var info = codec.Info;
                return Swaps(codec.EncodedOrigin) ? (info.Height, info.Width) : (info.Width, info.Height);
            }
        }

        public static byte[] LoadFile(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxImageBytes)
                throw new InvalidDataException("Choose a JPG, PNG, BMP or GIF image smaller than 30 MB.");
            return System.IO.File.ReadAllBytes(path);
        }

        /// <summary>Scales down (never up) to fit maxSide, flattens transparency onto white and re-encodes.</summary>
        public static byte[] Normalize(byte[] bytes, int maxSide = 1600, ImageKind format = ImageKind.Jpeg)
        {
            var loaded = Load(bytes, maxSide);
            using (var source = loaded.Bitmap)
            {
                double scale = Math.Min(1.0, (double)maxSide / Math.Max(loaded.Width, loaded.Height));
                int width = Math.Max(1, (int)Math.Round(loaded.Width * scale)), height = Math.Max(1, (int)Math.Round(loaded.Height * scale));
                using (var target = new SKBitmap(new SKImageInfo(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul)))
                {
                    using (var canvas = new SKCanvas(target))
                    using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
                    {
                        canvas.Clear(SKColors.White);
                        canvas.DrawBitmap(source, new SKRect(0, 0, width, height), paint);
                    }
                    return Encode(target, format);
                }
            }
        }

        // Center crop at native resolution; already-square pictures retain their original bytes.
        public static byte[] Square(byte[] bytes)
        {
            using (var source = Decode(bytes))
            {
                if (source.Width == source.Height) return bytes;
                int side = Math.Min(source.Width, source.Height);
                int left = (source.Width - side) / 2, top = (source.Height - side) / 2;
                bool jpeg = bytes.Length > 2 && bytes[0] == 0xff && bytes[1] == 0xd8;
                using (var square = new SKBitmap(new SKImageInfo(side, side, SKImageInfo.PlatformColorType, jpeg ? SKAlphaType.Opaque : SKAlphaType.Premul)))
                {
                    using (var canvas = new SKCanvas(square))
                    {
                        canvas.Clear(jpeg ? SKColors.Black : SKColors.Transparent);
                        canvas.DrawBitmap(source, new SKRect(left, top, left + side, top + side), new SKRect(0, 0, side, side));
                    }
                    return Encode(square, jpeg ? ImageKind.Jpeg : ImageKind.Png);
                }
            }
        }

        public static byte[] Thumbnail(byte[] bytes) => Normalize(bytes, 128, ImageKind.Png);

        public static ImageKind FormatForPath(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": return ImageKind.Png;
                case ".bmp": return ImageKind.Bmp;
                case ".gif": return ImageKind.Gif;
                default: return ImageKind.Jpeg;
            }
        }

        public static string MimeType(byte[] bytes) =>
            bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47 ? "image/png" : "image/jpeg";

        private readonly struct Loaded
        {
            internal readonly SKBitmap Bitmap; internal readonly int Width, Height;
            internal Loaded(SKBitmap bitmap, int width, int height) { Bitmap = bitmap; Width = width; Height = height; }
        }

        private static SKCodec OpenCodec(byte[] bytes, out SKData data)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImageBytes)
                throw new InvalidDataException("Choose an image smaller than 30 MB.");
            data = SKData.CreateCopy(bytes);
            var codec = SKCodec.Create(data);
            if (codec == null) { data.Dispose(); throw new InvalidDataException("The image format is not supported or the image is damaged."); }
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0) { codec.Dispose(); data.Dispose(); throw new InvalidDataException("The image has no readable dimensions."); }
            if ((long)info.Width * info.Height > MaxPixels) { codec.Dispose(); data.Dispose(); throw new InvalidDataException("Choose an image with fewer than 40 million pixels."); }
            return codec;
        }

        // minimumSide 0 decodes full size. Otherwise the codec may decode directly at a
        // reduced scale (JPEG DCT scaling) as long as the result still covers minimumSide.
        private static Loaded Load(byte[] bytes, int minimumSide)
        {
            using (var codec = OpenCodec(bytes, out var data))
            using (data)
            {
                var info = codec.Info;
                var size = new SKSizeI(info.Width, info.Height);
                int longest = Math.Max(info.Width, info.Height);
                if (minimumSide > 0 && longest > minimumSide)
                {
                    var scaled = codec.GetScaledDimensions((float)minimumSide / longest);
                    if (scaled.Width > 0 && scaled.Height > 0 && Math.Max(scaled.Width, scaled.Height) >= minimumSide) size = scaled;
                }
                var bitmap = DecodePixels(codec, size, info);
                if (bitmap == null && (size.Width != info.Width || size.Height != info.Height))
                    bitmap = DecodePixels(codec, new SKSizeI(info.Width, info.Height), info);
                if (bitmap == null) throw new InvalidDataException("The image could not be decoded.");
                var origin = codec.EncodedOrigin;
                bitmap = Orient(bitmap, origin);
                return Swaps(origin) ? new Loaded(bitmap, info.Height, info.Width) : new Loaded(bitmap, info.Width, info.Height);
            }
        }

        private static SKBitmap DecodePixels(SKCodec codec, SKSizeI size, SKImageInfo source)
        {
            var target = new SKImageInfo(size.Width, size.Height, SKImageInfo.PlatformColorType,
                source.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
            var bitmap = new SKBitmap(target);
            var result = codec.GetPixels(target, bitmap.GetPixels());
            if (result == SKCodecResult.Success || result == SKCodecResult.IncompleteInput) return bitmap;
            bitmap.Dispose();
            return null;
        }

        private static bool Swaps(SKEncodedOrigin origin) =>
            origin == SKEncodedOrigin.LeftTop || origin == SKEncodedOrigin.RightTop || origin == SKEncodedOrigin.RightBottom || origin == SKEncodedOrigin.LeftBottom;

        // Honor camera/image-editor EXIF orientation before re-encoding without metadata.
        private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
        {
            float w = source.Width, h = source.Height;
            SKMatrix matrix;
            switch (origin)
            {
                case SKEncodedOrigin.TopRight: matrix = new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1); break;
                case SKEncodedOrigin.BottomRight: matrix = new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1); break;
                case SKEncodedOrigin.BottomLeft: matrix = new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1); break;
                case SKEncodedOrigin.LeftTop: matrix = new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1); break;
                case SKEncodedOrigin.RightTop: matrix = new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1); break;
                case SKEncodedOrigin.RightBottom: matrix = new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1); break;
                case SKEncodedOrigin.LeftBottom: matrix = new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1); break;
                default: return source;
            }
            bool swap = Swaps(origin);
            var oriented = new SKBitmap(new SKImageInfo(swap ? source.Height : source.Width, swap ? source.Width : source.Height, source.ColorType, source.AlphaType));
            using (source)
            using (var canvas = new SKCanvas(oriented))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.SetMatrix(matrix);
                canvas.DrawBitmap(source, 0, 0);
            }
            return oriented;
        }

        private static byte[] Encode(SKBitmap bitmap, ImageKind kind)
        {
            switch (kind)
            {
                case ImageKind.Bmp: return EncodeBmp(bitmap);
                case ImageKind.Gif: return EncodeGif(bitmap);
            }
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(kind == ImageKind.Png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, kind == ImageKind.Png ? 100 : 92))
            {
                if (data == null) throw new InvalidDataException("The image could not be encoded.");
                return data.ToArray();
            }
        }

        private static byte[] Rgba(SKBitmap bitmap)
        {
            // Callers pass opaque (white-flattened) images, so premultiplied and straight alpha agree.
            using (var copy = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul)))
            {
                using (var canvas = new SKCanvas(copy)) { canvas.Clear(SKColors.White); canvas.DrawBitmap(bitmap, 0, 0); }
                return copy.GetPixelSpan().ToArray();
            }
        }

        private static byte[] EncodeBmp(SKBitmap bitmap)
        {
            int width = bitmap.Width, height = bitmap.Height, stride = (width * 3 + 3) & ~3;
            byte[] pixels = Rgba(bitmap);
            var output = new byte[54 + stride * height];
            output[0] = (byte)'B'; output[1] = (byte)'M';
            Int32(output, 2, output.Length); Int32(output, 10, 54); Int32(output, 14, 40);
            Int32(output, 18, width); Int32(output, 22, height);
            output[26] = 1; output[28] = 24;
            Int32(output, 34, stride * height); Int32(output, 38, 2835); Int32(output, 42, 2835);
            for (int y = 0; y < height; y++)
            {
                int row = 54 + (height - 1 - y) * stride, source = y * width * 4;
                for (int x = 0; x < width; x++, source += 4)
                {
                    output[row + x * 3] = pixels[source + 2];
                    output[row + x * 3 + 1] = pixels[source + 1];
                    output[row + x * 3 + 2] = pixels[source];
                }
            }
            return output;
        }

        private static void Int32(byte[] data, int offset, int value)
        { data[offset] = (byte)value; data[offset + 1] = (byte)(value >> 8); data[offset + 2] = (byte)(value >> 16); data[offset + 3] = (byte)(value >> 24); }

        // 8-bit GIF with a fixed 6x7x6 color cube and LZW compression (same scheme as gif.h).
        private static byte[] EncodeGif(SKBitmap bitmap)
        {
            int width = Math.Min(bitmap.Width, 65535), height = Math.Min(bitmap.Height, 65535);
            byte[] pixels = Rgba(bitmap);
            var output = new MemoryStream();
            void Byte(int value) => output.WriteByte((byte)value);
            void Short(int value) { Byte(value & 255); Byte(value >> 8); }
            foreach (char c in "GIF89a") Byte(c);
            Short(width); Short(height); Byte(0xF7); Byte(0); Byte(0);
            for (int i = 0; i < 256; i++)
            {
                if (i < 252) { Byte(i / 42 * 51); Byte(i / 6 % 7 * 255 / 6); Byte(i % 6 * 51); }
                else { Byte(0); Byte(0); Byte(0); }
            }
            Byte(0x2C); Short(0); Short(0); Short(width); Short(height); Byte(0);
            const int minCodeSize = 8, clearCode = 256;
            Byte(minCodeSize);
            var data = new List<byte>();
            int bitBuffer = 0, bitCount = 0;
            void Code(int code, int size)
            {
                bitBuffer |= code << bitCount; bitCount += size;
                while (bitCount >= 8) { data.Add((byte)bitBuffer); bitBuffer >>= 8; bitCount -= 8; }
            }
            var tree = new ushort[4096 * 256];
            int current = -1, codeSize = minCodeSize + 1, maxCode = clearCode + 1;
            Code(clearCode, codeSize);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int p = (y * bitmap.Width + x) * 4;
                    int next = (pixels[p] + 25) / 51 * 42 + (pixels[p + 1] * 6 + 127) / 255 * 6 + (pixels[p + 2] + 25) / 51;
                    if (current < 0) { current = next; continue; }
                    int child = tree[current * 256 + next];
                    if (child != 0) { current = child; continue; }
                    Code(current, codeSize);
                    tree[current * 256 + next] = (ushort)(++maxCode);
                    if (maxCode >= 1 << codeSize) codeSize++;
                    if (maxCode == 4095)
                    {
                        Code(clearCode, codeSize);
                        Array.Clear(tree, 0, tree.Length);
                        codeSize = minCodeSize + 1; maxCode = clearCode + 1;
                    }
                    current = next;
                }
            if (current >= 0) Code(current, codeSize);
            Code(clearCode, codeSize);
            Code(clearCode + 1, minCodeSize + 1);
            if (bitCount > 0) data.Add((byte)bitBuffer);
            for (int offset = 0; offset < data.Count; offset += 255)
            {
                int count = Math.Min(255, data.Count - offset);
                Byte(count);
                for (int i = 0; i < count; i++) Byte(data[offset + i]);
            }
            Byte(0); Byte(0x3B);
            return output.ToArray();
        }
    }

    // Cover bytes are usually identical across an album's tracks. Validate each distinct
    // image once per scan. Thread-safe because folders are scanned in parallel.
    internal sealed class ArtworkValidationCache
    {
        private readonly Dictionary<string, bool> images = new Dictionary<string, bool>();
        private readonly Queue<string> order = new Queue<string>();
        private int decoded;
        internal int Decoded => decoded;
        internal bool Valid(byte[] data)
        {
            string key = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(data));
            lock (images) if (images.TryGetValue(key, out bool known)) return known;
            System.Threading.Interlocked.Increment(ref decoded);
            bool valid = Artwork.IsValid(data);
            lock (images)
            {
                if (images.ContainsKey(key)) return valid;
                if (images.Count >= 256) images.Remove(order.Dequeue());
                images.Add(key, valid); order.Enqueue(key);
            }
            return valid;
        }
    }
}
