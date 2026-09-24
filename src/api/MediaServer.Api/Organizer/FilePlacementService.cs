using System.Security.Cryptography;
using MediaServer.Api.Data;
using MediaServer.Api.IO;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Organizer;

public sealed class InsufficientPlacementSpaceException(long required, long available, Exception? inner = null)
    : IOException($"Not enough space to keep seeding and organize a library copy: {required} bytes required, {available} bytes available. Free space and retry, or stop seeding and continue.", inner);

/// <summary>Places independent copies using private temporary output and a durable destination reservation.</summary>
public sealed class FilePlacementService(MediaServerDbContext database, IFilesystemInspector filesystem, IPlacementOutput outputFactory)
{
    public async Task PlaceAsync(SourceFile file, string from, string to, string relativeTarget, bool copy, CancellationToken ct)
    {
        file.OriginalRelativePath ??= file.RelativePath;
        if (file.PlacementPath is { } reserved && reserved != relativeTarget)
            throw new IOException("An earlier placement reserves a different destination; retry the original placement.");
        file.PlacementPath = relativeTarget;
        await database.SaveChangesAsync(ct);
        try { Directory.CreateDirectory(Path.GetDirectoryName(to)!); }
        catch (IOException e) when (copy && (e.HResult & 0xffff) is 28 or 39 or 112)
        {
            throw new InsufficientPlacementSpaceException(file.SizeBytes, filesystem.GetAvailableFreeBytes(Path.GetDirectoryName(to)!), e);
        }
        // Also clear interrupted output before a Copy-to-Move fallback or recovered promotion.
        var temp = to + $".ingest-{file.Id:N}.partial";
        if (File.Exists(temp)) File.Delete(temp);

        // Only a completed copy with a recorded digest may be recovered after a lost commit.
        if (File.Exists(to))
        {
            if (file.PlacementHash is not { } expected || await HashAsync(to, ct) != expected)
                throw new IOException($"Reserved destination is occupied and cannot be adopted safely: {relativeTarget}");
            return;
        }
        if (!copy)
        {
            File.Move(from, to); // No overwrite, and the engine must already be released by the caller.
            return;
        }

        var download = file.DownloadId is { } id ? await database.Downloads.FindAsync([id], ct) : null;
        var placedBytes = await database.SourceFiles.Where(x => x.IngestItemId == file.IngestItemId &&
            x.Id != file.Id && x.OriginalRelativePath != null && x.RelativePath != x.OriginalRelativePath).SumAsync(x => x.SizeBytes, ct);
        var available = filesystem.GetAvailableFreeBytes(Path.GetDirectoryName(to)!);
        var expectedLength = new FileInfo(from).Length;
        if (expectedLength != file.SizeBytes)
            throw new IOException("The completed torrent file length changed before placement.");
        if (expectedLength > available) throw new InsufficientPlacementSpaceException(expectedLength, available);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            var lastReport = DateTime.UtcNow;
            // Completed seed managers may retain write-capable handles on Windows. Completion is
            // checked by the pipeline; sharing those handles lets us read while uploading continues.
            await using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, true))
            await using (var output = outputFactory.Create(temp))
            {
                var buffer = new byte[1024 * 1024];
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    hash.AppendData(buffer, 0, count);
                    written += count;
                    if (download is not null && DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(1))
                    {
                        download.PlacementBytes = placedBytes + written;
                        await database.SaveChangesAsync(ct);
                        lastReport = DateTime.UtcNow;
                    }
                }
                await output.FlushAsync(ct);
                if (output is FileStream outputFile) outputFile.Flush(flushToDisk: true);
            }
            if (written != expectedLength || new FileInfo(temp).Length != expectedLength)
                throw new IOException("The library copy did not complete; original data is retained.");
            file.PlacementHash = Convert.ToHexString(hash.GetHashAndReset());
            await database.SaveChangesAsync(ct); // Digest precedes promotion, so interrupted commits can recover.
            File.Move(temp, to);
            if (download is not null) download.PlacementBytes = placedBytes + written;
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 28 or 39 or 112)
        {
            throw new InsufficientPlacementSpaceException(expectedLength,
                filesystem.GetAvailableFreeBytes(Path.GetDirectoryName(to)!), e);
        }
        finally
        {
            // If this fails, surface the cleanup error; do not hide an owned partial file.
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
}
