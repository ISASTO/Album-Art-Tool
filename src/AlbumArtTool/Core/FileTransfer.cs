using System;
using System.IO;
using System.Threading;

namespace AlbumArtTool.Core
{
    internal static class FileTransfer
    {
        internal static void Copy(string source, string target, CancellationToken token) => CopyCore(source, target, token, false);
        internal static string CopyAndHash(string source, string target, CancellationToken token) => CopyCore(source, target, token, true);
        private static string CopyCore(string source, string target, CancellationToken token, bool fingerprint)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            bool created = false; string digest = null;
            try
            {
                using (var hash = fingerprint ? System.Security.Cryptography.SHA256.Create() : null)
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    var buffer = new byte[65536]; int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    { token.ThrowIfCancellationRequested(); hash?.TransformBlock(buffer, 0, count, buffer, 0); output.Write(buffer, 0, count); }
                    if (hash != null) { hash.TransformFinalBlock(new byte[0], 0, 0); digest = Convert.ToBase64String(hash.Hash); }
                    output.Flush(true);
                }
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
                return digest;
            }
            catch { if (created) try { File.Delete(target); } catch { } throw; }
        }
    }
}
