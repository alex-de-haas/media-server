using MediaServer.Api.Configuration;
using System.Text.Json;
using MediaServer.Api.Bluray;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Organizer;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Transcoding;

/// <summary>An administrator's explicit playlist and track selection.</summary>
public sealed record CreateBlurayRequest(Guid SourceId, BluraySelection Selection, string? VersionName = null);

public sealed partial class VideoPartJoinService
{
    public async Task<BlurayInspection> InspectBlurayAsync(Guid sourceId, string? playlistId, CancellationToken ct)
    {
        if (!(await engine.GetToolingAsync(ct)).BlurayImport)
            throw new TranscodeRequestException("Connect a Transcode Engine with Blu-ray import support (MKVToolNix 81 or newer).");
        var source = await database.MediaSources.AsNoTracking().Include(s => s.MediaItem).ThenInclude(i => i!.Catalog)
            .SingleOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is not { Kind: MediaSourceKind.Bluray, MediaItem: { Kind: MediaKind.Movie, PublicId: not null, Catalog: { } catalog } } ||
            !sandbox.TryResolve(catalog, source.Path, out var absolute) ||
            !CatalogMounts.TryResolve(settings, absolute, out var label, out var relative))
            throw new TranscodeRequestException("The Blu-ray source is unavailable or not mounted in the engine.");
        try { return await engine.InspectBlurayAsync(label, relative, playlistId, ct); }
        catch (InvalidOperationException ex) { throw new TranscodeRequestException(ex.Message); }
    }

    public async Task<TranscodeJobResponse> CreateBlurayAsync(CreateBlurayRequest request, CancellationToken ct)
    {
        if (request.Selection is null) throw new TranscodeRequestException("Choose a playlist and tracks.");
        var inspection = await InspectBlurayAsync(request.SourceId, request.Selection.PlaylistId, ct);
        var playlist = inspection.Playlists.SingleOrDefault(p => p.Id == request.Selection.PlaylistId);
        if (inspection.Revision != request.Selection.Revision || playlist is null || playlist.Error is not null)
            throw new TranscodeRequestException(playlist?.Error ?? "The disc changed. Inspect it again before creating MKV.");
        var label = string.IsNullOrWhiteSpace(request.VersionName) ? "Blu-ray MKV" : request.VersionName.Trim();
        if (label.Length > 120 || label.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || label.Any(char.IsControl))
            throw new TranscodeRequestException("Use a version name without filename separators or control characters (up to 120 characters).");
        var id = Guid.NewGuid();
        ActiveOperations.TryAdd(id, 0);
        try
        {
            TranscodeJob job;
            TranscodeJobRequest engineRequest;
            using (await LibraryFileMutation.EnterAsync(ct))
            {
                var source = await database.MediaSources.Include(s => s.MediaItem).ThenInclude(i => i!.Catalog)
                    .SingleOrDefaultAsync(s => s.Id == request.SourceId, ct);
                if (source is not { Kind: MediaSourceKind.Bluray, MediaItem: { Kind: MediaKind.Movie, PublicId: not null, Catalog: { } catalog } item })
                    throw new TranscodeRequestException("Choose a published movie's Blu-ray source.");
                await LibraryFileMutation.RequireItemAvailableAsync(database, item.Id, ct);
                if (await moveGuard.IsItemMovingAsync(item.Id, ct)) throw new TranscodeConflictException(LibraryMoveGuard.MoveInProgressError);
                var output = LibraryNaming.ForMovie(catalog, item, ".mkv", label);
                var baseLabel = label;
                for (var suffix = 2; ; suffix++)
                {
                    if (!sandbox.TryResolve(catalog, output, out var path)) throw new TranscodeRequestException("Invalid output path.");
                    if (!File.Exists(path) && !Directory.Exists(path) &&
                        !await database.MediaSources.AnyAsync(s => s.MediaItem!.CatalogId == catalog.Id && s.Path == output, ct) &&
                        !await database.TranscodeJobs.AnyAsync(j => j.CatalogId == catalog.Id && j.OutputPath == output, ct)) break;
                    label = $"{baseLabel} {suffix}";
                    output = LibraryNaming.ForMovie(catalog, item, ".mkv", label);
                }
                job = new TranscodeJob
                {
                    Id = id, EngineJobId = id.ToString("N"), Kind = TranscodeJobKind.Bluray,
                    MediaSourceId = source.Id, MediaItemId = item.Id, CatalogId = catalog.Id,
                    InputPath = source.Path, OutputPath = output, Name = label,
                    VideoCodec = "copy", HardwareAcceleration = "none", State = TranscodeJobState.Queued,
                    CreatedAt = DateTimeOffset.UtcNow, ExpectedDurationSeconds = playlist.DurationSeconds,
                    BluraySelectionJson = JsonSerializer.Serialize(request.Selection),
                };
                engineRequest = RequestFor(job, catalog);
                database.TranscodeJobs.Add(job);
                await database.SaveChangesAsync(ct);
            }
            try { await SubmitAsync(job, engineRequest, ct); }
            catch (JoinRejectedException ex)
            {
                Fail(job, ex.Message);
                await database.SaveChangesAsync(CancellationToken.None);
                throw new TranscodeRequestException(ex.Message);
            }
            catch (Exception ex)
            {
                job.Error = "Waiting to confirm MKV creation; retrying with the same job id.";
                await database.SaveChangesAsync(CancellationToken.None);
                logger.LogWarning(ex, "Blu-ray job {Id} submission requires reconciliation.", id);
            }
            return TranscodeJobResponse.From(job, engine.GetSnapshot(job.EngineJobId));
        }
        finally { ActiveOperations.TryRemove(id, out _); }
    }
}
