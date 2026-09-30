using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using TagLib;

namespace AlbumArtTool.Core
{
    public static class Artwork
    {
        public const int MaxImageBytes = 30 * 1024 * 1024;
        private const long MaxPixels = 40000000;

        public static bool IsCover(IPicture picture) => picture.Type == PictureType.FrontCover || picture.Type == PictureType.Other;

        public static byte[] GetCover(TagLib.Tag tag)
        {
            foreach (var picture in tag.Pictures.Where(IsCover).OrderBy(p => p.Type == PictureType.FrontCover ? 0 : 1))
            {
                byte[] bytes = picture.Data.Data;
                if (bytes.Length == 0 || bytes.Length > MaxImageBytes) continue;
                try { using (var image = Decode(bytes)) return bytes; }
                catch (Exception e) when (IsImageError(e)) { }
            }
            return null;
        }

        public static bool IsImageError(Exception e) => e is ArgumentException || e is OutOfMemoryException ||
            e is InvalidDataException || e is System.Runtime.InteropServices.ExternalException;

        public static Bitmap Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImageBytes)
                throw new InvalidDataException("Choose an image smaller than 30 MB.");
            using (var stream = new MemoryStream(bytes))
            using (var source = Image.FromStream(stream, true, true))
            {
                if ((long)source.Width * source.Height > MaxPixels)
                    throw new InvalidDataException("Choose an image with fewer than 40 million pixels.");
                // Honor camera/image-editor EXIF orientation before stripping metadata.
                if (source.PropertyIdList.Contains(0x112))
                {
                    int orientation = source.GetPropertyItem(0x112).Value[0];
                    RotateFlipType[] transforms = { RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipNone,
                        RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate180FlipNone, RotateFlipType.Rotate180FlipX,
                        RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate270FlipX,
                        RotateFlipType.Rotate270FlipNone };
                    if (orientation < transforms.Length) source.RotateFlip(transforms[orientation]);
                }
                return new Bitmap(source);
            }
        }

        public static byte[] LoadFile(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxImageBytes)
                throw new InvalidDataException("Choose a JPG, PNG, BMP or GIF image smaller than 30 MB.");
            return System.IO.File.ReadAllBytes(path);
        }

        public static byte[] Normalize(byte[] bytes, int maxSide = 1600, ImageFormat format = null)
        {
            using (var source = Decode(bytes))
            {
                double scale = Math.Min(1.0, (double)maxSide / Math.Max(source.Width, source.Height));
                using (var bitmap = new Bitmap(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)), PixelFormat.Format24bppRgb))
                using (var graphics = Graphics.FromImage(bitmap))
                using (var output = new MemoryStream())
                {
                    graphics.Clear(Color.White);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    format = format ?? ImageFormat.Jpeg;
                    if (format.Guid == ImageFormat.Jpeg.Guid)
                    {
                        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                        using (var parameters = new EncoderParameters(1))
                        {
                            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                            bitmap.Save(output, encoder, parameters);
                        }
                    }
                    else bitmap.Save(output, format);
                    return output.ToArray();
                }
            }
        }

        public static byte[] Thumbnail(byte[] bytes) => Normalize(bytes, 128, ImageFormat.Png);

        public static ImageFormat FormatForPath(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": return ImageFormat.Png;
                case ".bmp": return ImageFormat.Bmp;
                case ".gif": return ImageFormat.Gif;
                default: return ImageFormat.Jpeg;
            }
        }
    }
}
