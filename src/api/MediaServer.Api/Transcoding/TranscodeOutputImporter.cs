using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Probe;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Transcoding;

/// <summary>
/// Turns a completed transcode output into a new <see cref="MediaSource"/> version of the same movie:
/// probes the output file and attaches a source (+ streams) to the original's media item, so the smaller
/// re-encode appears in the client's version picker for the operator to verify before deleting the original.
/// Mirrors how <see cref="Pipeline.Stages.ProbeStage"/> builds sources, but for one already-known item — no
/// re-identify, so the output can never be mistaken for a new movie. The source carries no
/// <see cref="MediaSource.SourceFileId"/> (it is not produced by an ingest/scan).
/// </summary>
public sealed class TranscodeOutputImporter(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    IMediaProbe probe,
    ILogger<TranscodeOutputImporter> logger)
{
    /// <summary>Probes the job's output and attaches it as a new movie version. Returns false when the
    /// output file is missing on disk (the caller should mark the job failed). Idempotent: a second call for
    /// the same output is a no-op.</summary>
    public async Task<bool> ImportAsync(TranscodeJob job, CancellationToken cancellationToken)
    {
        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == job.CatalogId, cancellationToken);
        if (catalog is null)
        {
            logger.LogWarning("Transcode job {JobId}: catalog {CatalogId} not found; cannot import output.", job.Id, job.CatalogId);
            return false;
        }

        if (!sandbox.TryResolve(catalog, job.OutputPath, out var absolute) || !File.Exists(absolute))
        {
            logger.LogWarning("Transcode job {JobId}: output '{Output}' is missing on disk.", job.Id, job.OutputPath);
            return false;
        }

        // The completion can be observed twice (the engine event and the reconcile tick, or across a restart);
        // dedup on (item, path) so the version is created exactly once.
        var alreadyImported = await database.MediaSources.AnyAsync(
            source => source.MediaItemId == job.MediaItemId && source.Path == job.OutputPath, cancellationToken);
        if (alreadyImported)
        {
            return true;
        }

        var result = await probe.ProbeAsync(absolute, cancellationToken);
        var source = new MediaSource
        {
            Id = Guid.NewGuid(),
            MediaItemId = job.MediaItemId,
            SourceFileId = null,
            VersionName = VersionLabel(job.VideoCodec),
            Container = result.Container,
            Path = job.OutputPath,
            SizeBytes = result.SizeBytes,
            Bitrate = result.Bitrate,
            DurationTicks = result.DurationTicks,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        database.MediaSources.Add(source);

        foreach (var stream in result.Streams)
        {
            database.MediaStreams.Add(new MediaStream
            {
                Id = Guid.NewGuid(),
                MediaSourceId = source.Id,
                StreamType = stream.Type,
                Index = stream.Index,
                Codec = stream.Codec,
                Profile = stream.Profile,
                Language = stream.Language,
                Width = stream.Width,
                Height = stream.Height,
                FrameRate = stream.FrameRate,
                BitDepth = stream.BitDepth,
                HdrFormat = stream.HdrFormat,
                Channels = stream.Channels,
                SampleRate = stream.SampleRate,
                IsDefault = stream.IsDefault,
                IsForced = stream.IsForced,
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Transcode job {JobId}: imported output as a new '{Version}' version of item {ItemId}.",
            job.Id, source.VersionName, job.MediaItemId);
        return true;
    }

    private static string VersionLabel(string codec) =>
        codec.Equals("hevc", StringComparison.OrdinalIgnoreCase) ? "HEVC" : "H.264";
}
