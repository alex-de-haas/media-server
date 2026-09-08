using System.Security.Claims;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Library;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Native;

public sealed record NativeEpisodeDto(EpisodeDto Episode, string? Still, long? DurationTicks);

/// <summary>Locally held episodes, with authenticated instance artwork and per-viewer progress.</summary>
public static class NativeEpisodeEndpoints
{
    public static void MapNativeEpisodeEndpoints(this RouteGroupBuilder group) =>
        group.MapGet("/items/{id:guid}/episodes", ReadAsync)
            .RequireAuthorization().WithName("getNativeEpisodes")
            .Produces<IReadOnlyList<NativeEpisodeDto>>()
            .Produces(StatusCodes.Status404NotFound);

    internal static async Task<IResult> ReadAsync(Guid id, Guid? seasonId, ClaimsPrincipal principal,
        MediaServerDbContext database, LibraryReadService library, MediaServerSettings settings, CancellationToken cancellationToken)
    {
        if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } userId)
            return Results.Unauthorized();
        if (!await database.MediaItems.AnyAsync(item => item.Id == id && item.Kind == MediaKind.Series &&
            item.PublicId != null && item.RemovedAt == null, cancellationToken))
            return Results.NotFound();
        if (seasonId is { } season && !await database.MediaItems.AnyAsync(item => item.Id == season &&
            item.SeriesId == id && item.Kind == MediaKind.Season && item.PublicId != null && item.RemovedAt == null,
            cancellationToken))
            return Results.NotFound();

        var visible = database.MediaItems.AsNoTracking().Where(item => item.SeriesId == id &&
            item.Kind == MediaKind.Episode && item.PublicId != null && item.RemovedAt == null &&
            (seasonId == null || item.SeasonId == seasonId) &&
            database.MediaItems.Any(parent => parent.Id == item.SeasonId && parent.Kind == MediaKind.Season && parent.SeriesId == id && parent.PublicId != null && parent.RemovedAt == null));
        var sources = await database.MediaSources.AsNoTracking()
            .Where(source => visible.Any(item => item.Id == source.MediaItemId))
            .Select(source => new { source.MediaItemId, source.Id, source.DurationTicks, source.CreatedAt,
                source.MediaItem!.DefaultSourceId }).ToListAsync(cancellationToken);
        var durations = sources.GroupBy(source => source.MediaItemId).ToDictionary(group => group.Key,
            group => group.OrderByDefault(group.First().DefaultSourceId, source => source.Id, source => source.CreatedAt)[0].DurationTicks);
        var assets = await database.ImageAssets.AsNoTracking()
            .Where(asset => asset.ImageType == ImageType.Backdrop && visible.Any(item => item.Id == asset.MediaItemId))
            .ToListAsync(cancellationToken);
        var stills = assets.GroupBy(asset => asset.MediaItemId).ToDictionary(group => group.Key,
            group => group.ToList().Best(ImageType.Backdrop, settings.PreferredLanguage));
        var episodes = await library.GetEpisodesAsync(id, seasonId, userId, cancellationToken);
        IReadOnlyList<NativeEpisodeDto> result = episodes.Where(episode => durations.ContainsKey(episode.Id)).Select(episode =>
        {
            var image = stills.GetValueOrDefault(episode.Id);
            var duration = episode.EpisodeNumberEnd > episode.EpisodeNumber && durations[episode.Id] > 0
                ? durations[episode.Id]
                : episode.RuntimeTicks is > 0 ? episode.RuntimeTicks : durations[episode.Id];
            return new NativeEpisodeDto(episode,
                image is null ? null : $"{NativeEndpoints.RoutePrefix}/items/{episode.Id:D}/images/backdrop?tag={image.Tag}",
                duration is > 0 ? duration : null);
        }).ToList();
        return Results.Ok(result);
    }

    internal static async Task<IReadOnlyList<SeasonSummaryDto>> AvailableSeasonsAsync(
        Guid seriesId, IReadOnlyList<SeasonSummaryDto> seasons, MediaServerDbContext database, CancellationToken ct)
    {
        var counts = await database.MediaItems.AsNoTracking()
            .Where(item => item.SeriesId == seriesId && item.Kind == MediaKind.Episode && item.PublicId != null &&
                item.RemovedAt == null && item.SeasonId != null &&
                database.MediaItems.Any(parent => parent.Id == item.SeasonId && parent.Kind == MediaKind.Season && parent.SeriesId == seriesId && parent.PublicId != null && parent.RemovedAt == null) &&
                database.MediaSources.Any(source => source.MediaItemId == item.Id))
            .GroupBy(item => item.SeasonId!.Value)
            .Select(group => new { Id = group.Key, Count = group.Count() }).ToDictionaryAsync(row => row.Id, row => row.Count, ct);
        return seasons.Where(season => counts.ContainsKey(season.Id))
            .Select(season => season with { EpisodeCount = counts[season.Id] }).ToList();
    }
}
