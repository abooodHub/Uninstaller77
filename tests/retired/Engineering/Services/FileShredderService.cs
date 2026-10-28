using System.IO;
using System.Security.Cryptography;

namespace WinUninstaller.Services;

/// <summary>
/// Secure multi-pass file shredder.
/// Overwrites contents with zeroes, ones, and cryptographic random bytes
/// before file system unlinking, preventing forensic file recovery.
/// </summary>
public static class FileShredderService
{
    public sealed record ShredResult(bool Success, int FilesShredded, long BytesOverwritten, string Message);

    public static ShredResult ShredPath(string path, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new ShredResult(false, 0, 0, "Invalid path.");

        // Critical safety checks
        var cleanPath = Path.GetFullPath(path);
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (cleanPath.StartsWith(winDir, StringComparison.OrdinalIgnoreCase) ||
            cleanPath.StartsWith(system32, StringComparison.OrdinalIgnoreCase))
        {
            return new ShredResult(false, 0, 0, "Refused to shred Windows system directory.");
        }

        int filesCount = 0;
        long bytesCount = 0;

        try
        {
            if (File.Exists(cleanPath))
            {
                bytesCount += ShredSingleFile(cleanPath);
                filesCount++;
                progress?.Report($"Shredded: {Path.GetFileName(cleanPath)}");
            }
            else if (Directory.Exists(cleanPath))
            {
                foreach (var file in Directory.GetFiles(cleanPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        bytesCount += ShredSingleFile(file);
                        filesCount++;
                        progress?.Report($"Shredded: {Path.GetFileName(file)}");
                    }
                    catch { }
                }
                Directory.Delete(cleanPath, recursive: true);
            }
            else
            {
                return new ShredResult(false, 0, 0, "Target does not exist.");
            }

            return new ShredResult(true, filesCount, bytesCount, $"Successfully shredded {filesCount} files ({bytesCount / 1024} KB).");
        }
        catch (Exception ex)
        {
            return new ShredResult(false, filesCount, bytesCount, $"Error during shredding: {ex.Message}");
        }
    }

    private static long ShredSingleFile(string filePath)
    {
        var fi = new FileInfo(filePath);
        if (fi.IsReadOnly) fi.IsReadOnly = false;
        long length = fi.Length;

        // Limit maximum single shred chunk buffer to 64 KB
        const int bufferSize = 65536;
        var buffer = new byte[bufferSize];

        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            // Pass 1: Overwrite with 0x00
            Array.Fill<byte>(buffer, 0x00);
            OverwriteFile(fs, length, buffer);

            // Pass 2: Overwrite with 0xFF
            Array.Fill<byte>(buffer, 0xFF);
            OverwriteFile(fs, length, buffer);

            // Pass 3: Overwrite with cryptographically secure random bytes
            fs.Position = 0;
            long written = 0;
            while (written < length)
            {
                int toWrite = (int)Math.Min(bufferSize, length - written);
                RandomNumberGenerator.Fill(buffer.AsSpan(0, toWrite));
                fs.Write(buffer, 0, toWrite);
                written += toWrite;
            }
            fs.Flush();

            // Truncate length
            fs.SetLength(0);
        }

        // Rename file to obscure original name in master file table
        var dir = Path.GetDirectoryName(filePath)!;
        var tempName = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        try { File.Move(filePath, tempName); File.Delete(tempName); }
        catch { File.Delete(filePath); }

        return length;
    }

    private static void OverwriteFile(FileStream fs, long length, byte[] pattern)
    {
        fs.Position = 0;
        long written = 0;
        while (written < length)
        {
            int toWrite = (int)Math.Min(pattern.Length, length - written);
            fs.Write(pattern, 0, toWrite);
            written += toWrite;
        }
        fs.Flush();
    }
}
