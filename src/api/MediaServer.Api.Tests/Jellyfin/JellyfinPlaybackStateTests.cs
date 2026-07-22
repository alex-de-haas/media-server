using System.Text.Json;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using MediaServer.Api.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Jellyfin;

/// <summary>
/// M3 playback-state coverage: the watched threshold + resume-reset policy, played/favorite toggles,
/// season/series rollups, and the resume / next-up queries.
/// </summary>
public sealed class JellyfinPlaybackStateTests : IDisposable
{
    private static readonly long MovieRuntime = TimeSpan.FromMinutes(120).Ticks;
    private static readonly long EpisodeRuntime = TimeSpan.FromMinutes(40).Ticks;

    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly TestTimeProvider _time = new(new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly UserDataService _userData;
    private readonly JellyfinLibraryService _library;

    private int _userId;
    private Guid _movieId;
    private string _moviePublicId = string.Empty;
    private Guid _seriesId;
    private string _seriesPublicId = string.Empty;
    private Guid _seasonId;
    private string _seasonPublicId = string.Empty;
    private readonly Guid[] _episodeIds = new Guid[3];
    private readonly string[] _episodePublicIds = new string[3];

    public JellyfinPlaybackStateTests()
    {
        var settings = new MediaServerSettings { SupportedLanguages = ["en-US"] };
        var hosty = new HostyOptions { AppId = "com.haas.media-server", CoreOrigin = "http://localhost:3001", AppDataDir = Path.GetTempPath() };
        var server = new JellyfinServerContext(hosty, settings);

        // The user-data service and the library share one scoped context, mirroring the request pipeline.
        _context = _db.Create();
        _userData = new UserDataService(_context, _time);
        _library = new JellyfinLibraryService(
            _context, new JellyfinItemMapper(server), new JellyfinCatalogArtwork(_context),
            new JellyfinCollectionService(_context), _userData, settings);
        Seed();
    }

    [Fact]
    public async Task Progress_below_threshold_stores_a_resume_point()
    {
        var position = (long)(MovieRuntime * 0.30);
        await _userData.ReportPlaybackAsync(_userId, _moviePublicId, position, isStopped: false, CancellationToken.None);

        var data = await DataForAsync(_movieId);
        Assert.False(data.Played);
        Assert.Equal(position, data.PlaybackPositionTicks);
        Assert.NotNull(data.PlayedPercentage);
        Assert.InRange(data.PlayedPercentage!.Value, 29, 31);
    }

    [Fact]
    public async Task Progress_past_the_watched_threshold_marks_played_and_clears_resume()
    {
        await _userData.ReportPlaybackAsync(_userId, _moviePublicId, (long)(MovieRuntime * 0.95), isStopped: false, CancellationToken.None);

        var data = await DataForAsync(_movieId);
        Assert.True(data.Played);
        Assert.Equal(0, data.PlaybackPositionTicks);
        Assert.Equal(1, data.PlayCount);
    }

    [Fact]
    public async Task Stopping_almost_immediately_discards_the_resume_point()
    {
        await _userData.ReportPlaybackAsync(_userId, _moviePublicId, (long)(MovieRuntime * 0.30), isStopped: false, CancellationToken.None);
        await _userData.ReportPlaybackAsync(_userId, _moviePublicId, (long)(MovieRuntime * 0.01), isStopped: true, CancellationToken.None);

        var data = await DataForAsync(_movieId);
        Assert.Equal(0, data.PlaybackPositionTicks);
        Assert.False(data.Played);
    }

    [Fact]
    public async Task Opening_a_watched_item_from_the_start_keeps_it_played()
    {
        await _userData.SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, CancellationToken.None);
        await _userData.ReportPlaybackAsync(_userId, _moviePublicId, 0, isStopped: false, CancellationToken.None);

        var data = await DataForAsync(_movieId);
        Assert.True(data.Played);
        Assert.Equal(0, data.PlaybackPositionTicks);
    }

    [Fact]
    public async Task Briefly_opening_a_watched_item_below_the_resume_floor_keeps_it_watched()
    {
        await _userData.SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, CancellationToken.None);

        // A few seconds of accidental playback, well under MinResumeThreshold, then a stop.
        await _userData.ReportPlaybackAsync(_userId, _moviePublicId, (long)(MovieRuntime * 0.02), isStopped: false, CancellationToken.None);
        await _userData.ReportPlaybackAsync(_userId, _moviePublicId, (long)(MovieRuntime * 0.02), isStopped: true, CancellationToken.None);

        var data = await DataForAsync(_movieId);
        Assert.True(data.Played);
        Assert.Equal(0, data.PlaybackPositionTicks);
    }

    [Fact]
    public async Task Re_watching_clears_the_watched_flag_when_a_new_resume_point_appears()
    {
        await _userData.SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, CancellationToken.None);
        var position = (long)(MovieRuntime * 0.20);
        await _userData.ReportPlaybackAsync(_userId, _moviePublicId, position, isStopped: false, CancellationToken.None);

        var data = await DataForAsync(_movieId);
        Assert.False(data.Played);
        Assert.Equal(position, data.PlaybackPositionTicks);
    }

    [Fact]
    public async Task Mark_played_then_unplayed_toggles_a_leaf()
    {
        var played = await _userData.SetPlayedAsync(_userId, _episodePublicIds[0], played: true, playedAt: null, CancellationToken.None);
        Assert.NotNull(played);
        Assert.True(played!.Played);
        Assert.Equal(1, played.PlayCount);

        var unplayed = await _userData.SetPlayedAsync(_userId, _episodePublicIds[0], played: false, playedAt: null, CancellationToken.None);
        Assert.NotNull(unplayed);
        Assert.False(unplayed!.Played);
        Assert.Equal(0, unplayed.PlaybackPositionTicks);
    }

    [Fact]
    public async Task Favorites_apply_to_leaves_and_folders()
    {
        var movieFav = await _userData.SetFavoriteAsync(_userId, _moviePublicId, favorite: true, CancellationToken.None);
        Assert.True(movieFav!.IsFavorite);

        var seriesFav = await _userData.SetFavoriteAsync(_userId, _seriesPublicId, favorite: true, CancellationToken.None);
        Assert.True(seriesFav!.IsFavorite);

        var cleared = await _userData.SetFavoriteAsync(_userId, _moviePublicId, favorite: false, CancellationToken.None);
        Assert.False(cleared!.IsFavorite);
    }

    [Fact]
    public async Task Season_and_series_rollups_reflect_episode_watched_state()
    {
        await _userData.SetPlayedAsync(_userId, _episodePublicIds[0], played: true, playedAt: null, CancellationToken.None);
        await _userData.SetPlayedAsync(_userId, _episodePublicIds[1], played: true, playedAt: null, CancellationToken.None);

        var season = await DataForAsync(_seasonId);
        Assert.False(season.Played);
        Assert.Equal(1, season.UnplayedItemCount);
        Assert.NotNull(season.PlayedPercentage);
        Assert.InRange(season.PlayedPercentage!.Value, 66, 67);

        var series = await DataForAsync(_seriesId);
        Assert.Equal(1, series.UnplayedItemCount);

        await _userData.SetPlayedAsync(_userId, _episodePublicIds[2], played: true, playedAt: null, CancellationToken.None);

        var fullSeason = await DataForAsync(_seasonId);
        Assert.True(fullSeason.Played);
        Assert.Equal(0, fullSeason.UnplayedItemCount);
    }

    [Fact]
    public async Task Marking_a_season_played_recurses_to_its_episodes()
    {
        await _userData.SetPlayedAsync(_userId, _seasonPublicId, played: true, playedAt: null, CancellationToken.None);

        foreach (var episodeId in _episodeIds)
        {
            var episode = await DataForAsync(episodeId);
            Assert.True(episode.Played);
        }

        var season = await DataForAsync(_seasonId);
        Assert.True(season.Played);
        Assert.Equal(0, season.UnplayedItemCount);
    }

    [Fact]
    public async Task Resume_lists_in_progress_items_newest_first_and_excludes_watched()
    {
        await _userData.ReportPlaybackAsync(_userId, _moviePublicId, (long)(MovieRuntime * 0.30), isStopped: false, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        await _userData.ReportPlaybackAsync(_userId, _episodePublicIds[0], (long)(EpisodeRuntime * 0.50), isStopped: false, CancellationToken.None);

        var resume = await _library.GetResumeAsync(_userId, parentPublicId: null, limit: 10, CancellationToken.None);
        Assert.Equal(2, resume.Items.Count);
        Assert.Equal(_episodePublicIds[0], resume.Items[0].Id);
        Assert.Equal(_moviePublicId, resume.Items[1].Id);

        // Finishing the movie removes it from the resume list.
        await _userData.ReportPlaybackAsync(_userId, _moviePublicId, (long)(MovieRuntime * 0.96), isStopped: false, CancellationToken.None);
        var afterFinish = await _library.GetResumeAsync(_userId, parentPublicId: null, limit: 10, CancellationToken.None);
        Assert.DoesNotContain(afterFinish.Items, item => item.Id == _moviePublicId);
    }

    [Fact]
    public async Task NextUp_returns_the_next_unwatched_episode_after_progress()
    {
        Assert.Empty((await _library.GetNextUpAsync(_userId, seriesPublicId: null, limit: 10, CancellationToken.None)).Items);

        await _userData.SetPlayedAsync(_userId, _episodePublicIds[0], played: true, playedAt: null, CancellationToken.None);
        var afterFirst = await _library.GetNextUpAsync(_userId, seriesPublicId: null, limit: 10, CancellationToken.None);
        Assert.Equal(_episodePublicIds[1], Assert.Single(afterFirst.Items).Id);

        await _userData.SetPlayedAsync(_userId, _episodePublicIds[1], played: true, playedAt: null, CancellationToken.None);
        var afterSecond = await _library.GetNextUpAsync(_userId, seriesPublicId: null, limit: 10, CancellationToken.None);
        Assert.Equal(_episodePublicIds[2], Assert.Single(afterSecond.Items).Id);

        await _userData.SetPlayedAsync(_userId, _episodePublicIds[2], played: true, playedAt: null, CancellationToken.None);
        var afterFinale = await _library.GetNextUpAsync(_userId, seriesPublicId: null, limit: 10, CancellationToken.None);
        Assert.Empty(afterFinale.Items);
    }

    [Fact]
    public async Task Library_item_surfaces_the_callers_user_data()
    {
        var position = (long)(MovieRuntime * 0.30);
        await _userData.ReportPlaybackAsync(_userId, _moviePublicId, position, isStopped: false, CancellationToken.None);

        var dto = await _library.GetItemAsync(_moviePublicId, includeMediaSources: false, _userId, CancellationToken.None);
        Assert.NotNull(dto);
        Assert.Equal(position, dto!.UserData!.PlaybackPositionTicks);
        Assert.False(dto.UserData.Played);
    }

    [Fact]
    public async Task Anonymous_user_gets_default_user_data()
    {
        using var context = _db.Create();
        var item = await FindItemAsync(context, _movieId);
        var userData = new UserDataService(context, _time);

        var map = await userData.LoadAsync(appUserId: null, [item], CancellationToken.None);
        var data = map[_movieId];
        Assert.Equal(_moviePublicId, data.Key);
        Assert.False(data.Played);
        Assert.Equal(0, data.PlaybackPositionTicks);
    }

    private async Task<UserItemDataDto> DataForAsync(Guid itemId)
    {
        // Read through a fresh context so assertions see committed state, not tracked entities.
        using var context = _db.Create();
        var item = await FindItemAsync(context, itemId);
        var userData = new UserDataService(context, _time);
        var map = await userData.LoadAsync(_userId, [item], CancellationToken.None);
        return map[itemId];
    }

    private static async Task<MediaItem> FindItemAsync(MediaServerDbContext context, Guid itemId)
    {
        var item = await context.MediaItems.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == itemId);
        return item!;
    }

    private void Seed()
    {
        var now = _time.GetUtcNow();
        using var context = _db.Create();

        var user = new AppUser
        {
            HostUserId = "host-1",
            Email = "alex@example.com",
            DisplayName = "Alex",
            Role = AppUserRole.User,
            CreatedAt = now,
            LastSeenAt = now,
        };
        context.AppUsers.Add(user);
        context.SaveChanges();
        _userId = user.Id;

        var movieCatalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = "/movies", CreatedAt = now, UpdatedAt = now };
        var seriesCatalog = new Catalog { Id = Guid.NewGuid(), Name = "Shows", Type = CatalogType.Series, Root = "/shows", CreatedAt = now, UpdatedAt = now };
        context.Catalogs.AddRange(movieCatalog, seriesCatalog);

        var movie = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = movieCatalog.Id,
            Kind = MediaKind.Movie,
            Title = "Inception",
            Year = 2010,
            AddedAt = now,
            UpdatedAt = now,
        };
        context.MediaItems.Add(movie);
        context.MediaSources.Add(NewSource(movie.Id, MovieRuntime, now));
        _movieId = movie.Id;
        _moviePublicId = movie.PublicId!;

        var series = new MediaItem { Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = seriesCatalog.Id, Kind = MediaKind.Series, Title = "Breaking Bad", AddedAt = now, UpdatedAt = now };
        var season = new MediaItem { Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = seriesCatalog.Id, Kind = MediaKind.Season, Title = "Season 1", ParentId = series.Id, SeriesId = series.Id, IndexNumber = 1, AddedAt = now, UpdatedAt = now };
        context.MediaItems.AddRange(series, season);
        _seriesId = series.Id;
        _seriesPublicId = series.PublicId!;
        _seasonId = season.Id;
        _seasonPublicId = season.PublicId!;

        for (var i = 0; i < 3; i++)
        {
            var episode = new MediaItem
            {
                Id = Guid.NewGuid(),
                PublicId = Guid.NewGuid().ToString("N"),
                CatalogId = seriesCatalog.Id,
                Kind = MediaKind.Episode,
                Title = $"Episode {i + 1}",
                ParentId = season.Id,
                SeriesId = series.Id,
                SeasonId = season.Id,
                ParentIndexNumber = 1,
                IndexNumber = i + 1,
                AddedAt = now,
                UpdatedAt = now,
            };
            context.MediaItems.Add(episode);
            context.MediaSources.Add(NewSource(episode.Id, EpisodeRuntime, now));
            _episodeIds[i] = episode.Id;
            _episodePublicIds[i] = episode.PublicId!;
        }

        context.SaveChanges();
    }

    private static MediaSource NewSource(Guid itemId, long runtimeTicks, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        MediaItemId = itemId,
        Container = "matroska",
        Path = $"library/{itemId:N}.mkv",
        SizeBytes = 1_000,
        DurationTicks = runtimeTicks,
        CreatedAt = now,
    };

    // ---- Phase 0 playback diagnostics (docs/planning/trakt-watched-state-sync.md) ----

    [Fact]
    public async Task PlaybackDiagnostics_AreOffByDefault_AndRecordNothing()
    {
        var diagnostics = new PlaybackDiagnostics(writer: null);

        Assert.False(diagnostics.Enabled);
        diagnostics.BeginRequest(PlaybackRouteKinds.Progress, _userId, _moviePublicId, 1, null, null, null, false, null, false);
        // Completing without a writer must be a no-op rather than throwing: diagnostics can never be
        // allowed to fail a playback request.
        await diagnostics.CompleteAsync(204, CancellationToken.None);
    }

    [Fact]
    public async Task ProgressReport_RecordsBeforeAndAfterState_WithoutChangingBehaviour()
    {
        var (diagnostics, path) = CreateDiagnostics();
        try
        {
            var position = (long)(MovieRuntime * 0.95);
            diagnostics.BeginRequest(
                PlaybackRouteKinds.Progress, _userId, _moviePublicId, position,
                playSessionId: "session-1", mediaSourceId: "source-1", isPaused: false, isStopped: false,
                datePlayed: null, datePlayedSupplied: false);

            await _userData.ReportPlaybackAsync(_userId, _moviePublicId, position, isStopped: false, diagnostics, CancellationToken.None);
            await diagnostics.CompleteAsync(204, CancellationToken.None);

            var record = ReadSingleRecord(path);
            Assert.Equal("Progress", record.GetProperty("route").GetString());
            Assert.Equal(204, record.GetProperty("status").GetInt32());
            Assert.Equal("session-1", record.GetProperty("playSessionId").GetString());
            Assert.Equal("source-1", record.GetProperty("mediaSourceId").GetString());
            Assert.Equal(MovieRuntime, record.GetProperty("runtimeTicks").GetInt64());
            Assert.Equal(0.95, record.GetProperty("positionFraction").GetDouble(), 2);
            // Crossing the threshold: not watched before, watched after, count incremented once.
            Assert.False(record.GetProperty("playedBefore").GetBoolean());
            Assert.True(record.GetProperty("playedAfter").GetBoolean());
            Assert.Equal(0, record.GetProperty("playCountBefore").GetInt32());
            Assert.Equal(1, record.GetProperty("playCountAfter").GetInt32());

            // The observed operation still applied exactly as it does without diagnostics.
            var row = await _context.UserItemData.AsNoTracking()
                .SingleAsync(data => data.AppUserId == _userId && data.MediaItemId == _movieId);
            Assert.True(row.Played);
            Assert.Equal(0, row.PlaybackPositionTicks);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ManualMarkThenUnmark_RecordsTheRetainedPlayCount()
    {
        var (diagnostics, path) = CreateDiagnostics();
        try
        {
            diagnostics.BeginRequest(
                PlaybackRouteKinds.PlayedItemsPost, _userId, _moviePublicId, null, null, null, null, false,
                datePlayed: null, datePlayedSupplied: false);
            await _userData.SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, diagnostics, CancellationToken.None);
            await diagnostics.CompleteAsync(200, CancellationToken.None);

            diagnostics.BeginRequest(
                PlaybackRouteKinds.PlayedItemsDelete, _userId, _moviePublicId, null, null, null, null, false,
                datePlayed: null, datePlayedSupplied: false);
            await _userData.SetPlayedAsync(_userId, _moviePublicId, played: false, playedAt: null, diagnostics, CancellationToken.None);
            await diagnostics.CompleteAsync(200, CancellationToken.None);

            var records = ReadRecords(path);
            Assert.Equal(2, records.Count);

            Assert.Equal("PlayedItemsPost", records[0].GetProperty("route").GetString());
            Assert.False(records[0].GetProperty("playedBefore").GetBoolean());
            Assert.True(records[0].GetProperty("playedAfter").GetBoolean());
            Assert.Equal(1, records[0].GetProperty("playCountAfter").GetInt32());

            // The unmark leaves PlayCount at 1 — the Played=false/PlayCount>0 residue the Trakt plan
            // must not export. Seeing it in the trace is the point of the exercise.
            Assert.Equal("PlayedItemsDelete", records[1].GetProperty("route").GetString());
            Assert.True(records[1].GetProperty("playedBefore").GetBoolean());
            Assert.False(records[1].GetProperty("playedAfter").GetBoolean());
            Assert.Equal(1, records[1].GetProperty("playCountBefore").GetInt32());
            Assert.Equal(1, records[1].GetProperty("playCountAfter").GetInt32());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Records_CarryNoTitlesPathsOrCredentials()
    {
        var (diagnostics, path) = CreateDiagnostics();
        try
        {
            diagnostics.BeginRequest(
                PlaybackRouteKinds.PlayedItemsPost, _userId, _moviePublicId, null, null, null, null, false,
                datePlayed: new DateTimeOffset(2026, 7, 22, 17, 54, 0, TimeSpan.Zero), datePlayedSupplied: true);
            await _userData.SetPlayedAsync(_userId, _moviePublicId, played: true, playedAt: null, diagnostics, CancellationToken.None);
            await diagnostics.CompleteAsync(200, CancellationToken.None);

            var line = File.ReadAllText(path);
            Assert.DoesNotContain("Interstellar", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/media", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", line, StringComparison.OrdinalIgnoreCase);

            var record = ReadSingleRecord(path);
            Assert.True(record.GetProperty("datePlayedSupplied").GetBoolean());
            Assert.Equal(_userId, record.GetProperty("appUserId").GetInt32());
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static (PlaybackDiagnostics Diagnostics, string Path) CreateDiagnostics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"playback-diagnostics-{Guid.NewGuid():N}.log");
        var writer = new PlaybackDiagnosticsWriter(path, NullLogger<PlaybackDiagnosticsWriter>.Instance);
        return (new PlaybackDiagnostics(writer), path);
    }

    private static List<JsonElement> ReadRecords(string path) =>
        File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

    private static JsonElement ReadSingleRecord(string path) => Assert.Single(ReadRecords(path));

    private static void Cleanup(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }


    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }
}
