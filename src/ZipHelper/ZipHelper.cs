using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace NOBlackBox
{
    public static class ZipHelper
    {
        // Tomo's Nukes 2026-10-21 (Tomo: "make sure that the zips don't break"): the zip is written as
        // "<name>.zip.acmi.part" and gets its real name only once it is complete, so a zip cut short (the game stopped
        // or killed while zipping) never looks like a finished recording. The poster and the live feed only pick up
        // names ending .zip.acmi or .acmi. Plain blocking calls: the recorder runs them on a worker thread, or on the
        // spot while the game is quitting, so the zip is done before the game exits.
        public static string ZipFile(
            string filePath,
            int maxRetries = 5,
            int initialDelayMs = 16)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException("File not found.", filePath);

            // Wait until file is free (with exponential backoff)
            WaitForFileAccess(filePath, maxRetries, initialDelayMs);

            string zipPath = Path.ChangeExtension(filePath, ".zip.acmi");
            string partPath = zipPath + ".part";
            if (File.Exists(partPath))
                File.Delete(partPath);

            using (FileStream zipStream = new FileStream(
                partPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920))
            {
                using (ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    ZipArchiveEntry entry = archive.CreateEntry(
                        Path.GetFileName(filePath),
                        CompressionLevel.Optimal);

                    // Open once and hold the handle during the copy
                    using (FileStream inputStream = new FileStream(
                        filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 81920))
                    using (Stream entryStream = entry.Open())
                    {
                        inputStream.CopyTo(entryStream, 81920);
                    }
                }
                zipStream.Flush(true);   // on disk before it takes the real name
            }

            if (File.Exists(zipPath))
                File.Delete(zipPath);
            File.Move(partPath, zipPath);
            return zipPath;
        }

        // 2026-10-21: a zip cut short when the game stopped is left as <name>.zip.acmi.part. The recorder calls this when it
        // loads, before anything records, so every .part found is stale; the raw .acmi beside it is complete and is
        // left alone (the poster zips it). Returns how many were deleted.
        public static int DeleteStaleParts(string dir)
        {
            int deleted = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    return 0;
                foreach (string part in Directory.GetFiles(dir, "*.part"))
                {
                    if (!part.EndsWith(".zip.acmi.part", StringComparison.Ordinal))
                        continue;
                    try
                    {
                        File.Delete(part);
                        deleted++;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return deleted;
        }

        private static void WaitForFileAccess(
            string filePath,
            int maxRetries,
            int initialDelayMs)
        {
            int delay = initialDelayMs;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using (new FileStream(
                        filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None))
                    {
                        // Successfully acquired exclusive access
                        return;
                    }
                }
                catch (IOException) when (attempt < maxRetries)
                {
                    Thread.Sleep(delay);
                    delay *= 2; // exponential backoff
                }
            }

            throw new IOException(
                $"The file '{filePath}' is still in use after {maxRetries} retries.");
        }

        /// <summary>
        /// Deletes a file with exponential backoff to avoid sharing violations.
        /// </summary>
        public static void DeleteFile(
            string filePath,
            int maxRetries = 5,
            int initialDelayMs = 16)
        {
            WaitForFileAccess(filePath, maxRetries, initialDelayMs);

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

            if (!File.Exists(filePath))
                return; // already gone — nothing to do

            int delay = initialDelayMs;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    File.Delete(filePath);
                    return; // success
                }
                catch (IOException) when (attempt < maxRetries)
                {
                    Thread.Sleep(delay);
                    delay *= 2; // exponential backoff
                }
                catch (UnauthorizedAccessException) when (attempt < maxRetries)
                {
                    Thread.Sleep(delay);
                    delay *= 2;
                }
            }

            throw new IOException(
                $"Failed to delete file '{filePath}' after {maxRetries} retries.");
        }
    }
}
