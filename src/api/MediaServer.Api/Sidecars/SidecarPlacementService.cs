using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using MediaServer.Api.Mux;
using MediaServer.Api.Probe;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Sidecars;

/// <summary>
/// Places a release's external audio tracks and subtitles beside the library file they belong to, and
/// records them as external streams of its media source.
/// <para>
/// This replaces merging them in during ingest. Nothing is re-written and nothing is discarded: a track
/// arrives as a file and stays one, whether or not the transcode engine is attached, so ingest gains no
/// dependency and behaves the same either way. Merging is a separate, later operation.
/// </para>
/// <para>
/// A sidecar audio track is preserved but not playable — Infuse has no external-audio support by any route,
/// and neither has Jellyfin — so for audio, merging is what makes a track usable rather than an optional
/// nicety. Sidecar subtitles are the opposite: clients read those from disk, which is why the names follow
/// the convention they match on.
/// </para>
/// </summary>
public sealed class SidecarPlacementService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    IMediaProbe probe,
    ILogger<SidecarPlacementService> logger)
{
    public async Task PlaceAsync(IReadOnlyList<SourceFile> sourceFiles, Catalog catalog, CancellationToken cancellationToken)
    {
        var groups = sourceFiles
            .Where(file => file.MediaItemId is not null)
            .GroupBy(file => file.MediaItemId!.Value);

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PlaceForItemAsync(group.Key, [.. group], catalog, cancellationToken);
        }
    }

    private async Task PlaceForItemAsync(
        Guid mediaItemId, IReadOnlyList<SourceFile> files, Catalog catalog, CancellationToken cancellationToken)
    {
        // Ordered so names and any ordinal fallback are the same on a re-drive.
        var companions = files
            .Where(file => file.AssignmentStatus == SourceFileAssignmentStatus.Confirmed &&
                MediaFormats.IsCompanion(file.RelativePath))
            .OrderBy(file => file.TorrentFileIndex ?? int.MaxValue)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();
        if (companions.Count == 0)
        {
            return;
        }

        // The video these belong beside. It has been organized by now, so its path is the canonical one.
        var video = files
            .Where(file => MediaFormats.IsPlayableMedia(file.RelativePath, file.SizeBytes))
            .OrderBy(file => file.TorrentFileIndex ?? int.MaxValue)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .FirstOrDefault();
        if (video is null)
        {
            // A dub-only release whose episodes are not in this batch — or specials whose videos ship in a
            // folder of their own. These used to be discarded with the staging leftovers; now they are kept
            // where they are so nothing is destroyed, and can be attached once their video arrives.
            logger.LogInformation(
                "Item {MediaItem} has {Count} companion file(s) but no video in this ingest; leaving them in place.",
                mediaItemId, companions.Count);
            return;
        }

        var source = await database.MediaSources
            .FirstOrDefaultAsync(candidate => candidate.MediaItemId == mediaItemId && candidate.Path == video.RelativePath, cancellationToken);
        if (source is null)
        {
            logger.LogWarning(
                "No media source for {Path}; its companion files are left where they are.", video.RelativePath);
            return;
        }

        var labelled = new List<(SourceFile File, string? Language, string? Title)>(companions.Count);
        foreach (var companion in companions)
        {
            labelled.Add(await LabelAsync(companion, video.RelativePath, catalog, cancellationToken));
        }

        var folder = FolderOf(video.RelativePath);
        var existing = await database.MediaStreams
            .Where(stream => stream.MediaSourceId == source.Id && stream.IsExternal)
            .ToListAsync(cancellationToken);

        // Captured before the moves, because placing a companion overwrites its RelativePath with the
        // canonical one — reading the staging roots afterwards would find none and leave every emptied
        // .incoming/<downloadId> folder behind.
        var stagingRoots = companions
            .Select(companion => StagingRootOf(companion.RelativePath))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var placed = 0;
        foreach (var named in SidecarNaming.For(Path.GetFileName(video.RelativePath), labelled))
        {
            var target = folder.Length == 0 ? named.FileName : $"{folder}/{named.FileName}";
            if (existing.Any(stream => string.Equals(stream.ExternalPath, target, StringComparison.Ordinal)))
            {
                continue; // Already placed by an earlier drive.
            }

            if (!TryMove(catalog, named.Source, target))
            {
                continue;
            }

            var (_, language, title) = labelled.First(entry => entry.File.Id == named.Source.Id);
            database.MediaStreams.Add(new MediaStream
            {
                Id = Guid.NewGuid(),
                MediaSourceId = source.Id,
                StreamType = MediaFormats.IsCompanionAudio(target) ? StreamType.Audio : StreamType.Subtitle,
                // External streams are not part of the container's own numbering, so they continue past it
                // rather than colliding with an embedded track's index.
                Index = 1000 + placed,
                Language = language,
                Title = title,
                IsExternal = true,
                ExternalPath = target,
            });

            named.Source.RelativePath = target;
            named.Source.AssignmentStatus = SourceFileAssignmentStatus.Sidecar;
            named.Source.UpdatedAt = DateTimeOffset.UtcNow;
            placed++;
        }

        if (placed > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Placed {Count} companion file(s) beside {Video}.", placed, video.RelativePath);
            SweepEmptiedStaging(catalog, stagingRoots);
        }
    }

    /// <summary>
    /// Removes the staging folders the placed files came out of. Organize deliberately spares any root that
    /// still holds a companion — its recursive sweep would otherwise take the only copy of a dub with it —
    /// so clearing what is now empty falls here.
    /// </summary>
    private void SweepEmptiedStaging(Catalog catalog, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (!sandbox.TryResolve(catalog, root, out var absolute) || !Directory.Exists(absolute))
            {
                continue;
            }

            try
            {
                if (!Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories).Any())
                {
                    Directory.Delete(absolute, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(exception, "Could not clear the emptied staging folder {Path}.", root);
            }
        }
    }

    /// <summary>The <c>.incoming/&lt;downloadId&gt;</c> staging root of a path, or null when it is not staged.</summary>
    private static string? StagingRootOf(string relativePath)
    {
        var parts = relativePath.Split('/');
        return parts.Length >= 2 && parts[0] == ".incoming" ? $"{parts[0]}/{parts[1]}" : null;
    }

    /// <summary>
    /// The language and title to record. A tagged container states its own — a <c>.mka</c> carries both
    /// internally, which is why the file name never has to be the source of truth — and an elementary
    /// stream (<c>.ac3</c>, <c>.dts</c>) has nowhere to put them, so the path is read instead.
    /// </summary>
    private async Task<(SourceFile File, string? Language, string? Title)> LabelAsync(
        SourceFile companion, string videoRelativePath, Catalog catalog, CancellationToken cancellationToken)
    {
        var fromPathLanguage = AudioTrackLabeler.InferLanguage(companion.RelativePath);
        var fromPathTitle = AudioTrackLabeler.InferTitle(companion.RelativePath, videoRelativePath);

        if (!sandbox.TryResolve(catalog, companion.RelativePath, out var absolute) || !File.Exists(absolute))
        {
            return (companion, fromPathLanguage, fromPathTitle);
        }

        try
        {
            var result = await probe.ProbeAsync(absolute, cancellationToken);
            var stream = result.Streams.FirstOrDefault();
            return (
                companion,
                TaggedLanguage(stream?.Language) ?? fromPathLanguage,
                Tagged(stream?.Title) ?? fromPathTitle);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An untagged elementary stream is not a failure — it is the case the path inference exists for.
            logger.LogDebug(exception, "Could not read tags from {Path}; using its path instead.", companion.RelativePath);
            return (companion, fromPathLanguage, fromPathTitle);
        }
    }

    /// <summary>A tag that is actually usable: a probe reports an absent one as null, but some containers
    /// carry an empty string instead, and either must yield to what the path says.</summary>
    private static string? Tagged(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Like <see cref="Tagged"/>, plus <c>und</c> — Matroska's "undetermined", which many muxers
    /// write by default — counts as untagged so the path-inferred language can replace it.</summary>
    private static string? TaggedLanguage(string? value) =>
        Tagged(value) is { } language && !language.Equals("und", StringComparison.OrdinalIgnoreCase) ? language : null;

    private bool TryMove(Catalog catalog, SourceFile companion, string targetRelative)
    {
        if (string.Equals(companion.RelativePath, targetRelative, StringComparison.Ordinal))
        {
            return true;
        }

        if (!sandbox.TryResolve(catalog, companion.RelativePath, out var from) || !File.Exists(from))
        {
            logger.LogWarning("Companion file missing on disk, not placed: {Path}", companion.RelativePath);
            return false;
        }

        if (!sandbox.TryResolve(catalog, targetRelative, out var to))
        {
            logger.LogWarning("Refusing to place a companion outside the catalog: {Path}", targetRelative);
            return false;
        }

        if (File.Exists(to))
        {
            // Never clobber a payload file that is already there.
            logger.LogWarning("Refusing to place {Companion}: {Target} already exists.", companion.RelativePath, targetRelative);
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not place companion {Path}.", companion.RelativePath);
            return false;
        }
    }

    private static string FolderOf(string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : relativePath[..lastSlash];
    }
}
