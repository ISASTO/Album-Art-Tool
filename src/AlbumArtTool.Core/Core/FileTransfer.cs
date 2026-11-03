using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace AlbumArtTool.Core
{
    internal static class FileTransfer
    {
        private const int BufferSize = 1 << 20;

        internal static void Copy(string source, string target, CancellationToken token) => CopyCore(source, target, token, false);
        internal static string CopyAndHash(string source, string target, CancellationToken token) => CopyCore(source, target, token, true);

        private static string CopyCore(string source, string target, CancellationToken token, bool fingerprint)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            bool created = false; string digest = null;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                using (var hash = fingerprint ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null)
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
                {
                    created = true;
                    try { output.SetLength(input.Length); output.Position = 0; } catch (IOException) { }
                    int count;
                    while ((count = input.Read(buffer, 0, BufferSize)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        hash?.AppendData(buffer, 0, count);
                        output.Write(buffer, 0, count);
                    }
                    output.SetLength(output.Position);
                    if (hash != null) digest = Convert.ToBase64String(hash.GetHashAndReset());
                    output.Flush(true);
                }
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
                return digest;
            }
            catch { if (created) try { File.Delete(target); } catch { } throw; }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        // SHA-256 of a whole file, base64 encoded. The format is persisted in Undo journals.
        internal static string Hash(string path) => Convert.ToBase64String(HashBytes(path));

        internal static byte[] HashBytes(string path)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                {
                    int count;
                    while ((count = stream.Read(buffer, 0, BufferSize)) > 0) hash.AppendData(buffer, 0, count);
                    return hash.GetHashAndReset();
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
    }
}
