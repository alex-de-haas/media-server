using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Probe;
using MediaServer.Api.Sidecars;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Transcoding;

/// <summary>
/// Turns a completed extraction's files into external <see cref="MediaStream"/> rows on the source they came
/// out of — the counterpart to <see cref="TranscodeOutputImporter"/>, which attaches a conversion's single
/// output as a new version instead.
/// <para>
/// What it writes is indistinguishable from a sidecar a release shipped, which is the point: everything the
/// sidecar machinery already does then applies without a line written for it.
/// </para>
/// </summary>
public sealed class ExtractOutputImporter(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    IMediaProbe probe,
    ILogger<ExtractOutputImporter> logger)
{
    /// <summary>
    /// Records every file the job produced. Returns false when any of them is missing on disk — the caller
    /// then marks the job failed — but the ones that <b>are</b> there are still imported first, and the job's
    /// error names what was not. Leaving a produced file with no row pointing at it is the one outcome the
    /// sidecar model exists to prevent, so a partial result is recorded rather than discarded.
    /// <para>
    /// Idempotent: a completion observed twice (the engine event and the reconcile tick, or across a restart)
    /// dedups on the source and path, exactly as placing a companion does.
    /// </para>
    /// </summary>
    public async Task<bool> ImportAsync(TranscodeJob job, CancellationToken cancellationToken)
    {
        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == job.CatalogId, cancellationToken);
        if (catalog is null)
        {
            logger.LogWarning("Extraction job {JobId}: catalog {CatalogId} not found; cannot import its tracks.", job.Id, job.CatalogId);
            return false;
        }

        var outputs = await database.TranscodeJobOutputs
            .Where(output => output.TranscodeJobId == job.Id)
            .OrderBy(output => output.SourceStreamIndex)
            .ToListAsync(cancellationToken);
        if (outputs.Count == 0)
        {
            logger.LogWarning("Extraction job {JobId} recorded no outputs to import.", job.Id);
            return false;
        }

        var existing = await database.MediaStreams
            .Where(stream => stream.MediaSourceId == job.MediaSourceId && stream.IsExternal)
            .ToListAsync(cancellationToken);
        var nextIndex = ExternalStreamIndex.NextFor(existing);

        var missing = new List<string>();
        var imported = 0;
        foreach (var output in outputs)
        {
            if (existing.Any(stream => string.Equals(stream.ExternalPath, output.RelativePath, StringComparison.Ordinal)))
            {
                continue; // Already imported by an earlier observation of this completion.
            }

            if (!sandbox.TryResolve(catalog, output.RelativePath, out var absolute) || !File.Exists(absolute))
            {
                missing.Add(Path.GetFileName(output.RelativePath));
                continue;
            }

            // Probed for the same reason a placed companion is: so the row reads like any other track —
            // "rus AC3 5.1 · 48 kHz" — rather than being the one kind with nothing but a name.
            var result = await probe.ProbeAsync(absolute, cancellationToken);
            var track = result.Streams.FirstOrDefault(stream => stream.Type == output.StreamType)
                ?? result.Streams.FirstOrDefault();

            database.MediaStreams.Add(new MediaStream
            {
                Id = Guid.NewGuid(),
                MediaSourceId = job.MediaSourceId,
                StreamType = output.StreamType,
                Index = nextIndex++,
                // The label the track was extracted under, not what the file can be read back as: a .srt has
                // nowhere to hold a language, so re-reading one would unlabel every extracted subtitle.
                Language = output.Language,
                Title = output.Title,
                Codec = track?.Codec,
                Channels = track?.Channels,
                SampleRate = track?.SampleRate,
                Bitrate = track?.Bitrate,
                IsExternal = true,
                ExternalPath = output.RelativePath,
            });
            imported++;
        }

        if (imported > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Extraction job {JobId}: recorded {Count} track(s) beside {Input}.", job.Id, imported, job.InputPath);
        }

        if (missing.Count > 0)
        {
            job.Error ??= $"The extraction finished but did not produce {string.Join(", ", missing)}.";
            return false;
        }

        return true;
    }
}
