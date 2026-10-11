using MediaServer.Api.Configuration;
using MediaServer.Api.Remux;
using MediaServer.Api.Native.Playback;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using MediaServer.Api.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;

namespace MediaServer.Api.Tests.Library;

public sealed class ManagementServiceTests : IDisposable
{
    private readonly JellyfinDatabase database = new();
    private readonly Guid catalogId = Guid.NewGuid();
    private readonly ManagementService service;

    public ManagementServiceTests()
    {
        database.Context.Catalogs.Add(new Catalog { Id = catalogId, Name = "Movies", Root = "/media", Type = CatalogType.Movie });
        database.Context.SaveChanges();
        service = new(database.Context, new MediaServerSettings { SupportedLanguages = ["en-US"] });
    }

    private MediaItem Item(string title, MediaKind kind = MediaKind.Movie, Guid? seriesId = null)
    {
        var item = new MediaItem { Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString(), Title = title,
            CatalogId = catalogId, Kind = kind, SeriesId = seriesId };
        database.Context.MediaItems.Add(item);
        return item;
    }

    private MediaSource Source(MediaItem item, string container = "mp4", int? profile = null, int height = 1080, bool probe = true)
    {
        var source = new MediaSource { Id = Guid.NewGuid(), MediaItemId = item.Id, Container = container,
            Path = "movie." + container, SizeBytes = 1000, Kind = container == "bdmv" ? MediaSourceKind.Bluray : MediaSourceKind.File };
        if (probe && container != "bdmv")
        {
            source.Streams.Add(new MediaStream { Id = Guid.NewGuid(), StreamType = StreamType.Video, Index = 0,
                Codec = "hevc", Height = height, Width = height == 2160 ? 3840 : 1920,
                HdrFormat = profile == null ? "SDR" : "Dolby Vision", DvProfile = profile });
            source.Streams.Add(new MediaStream { Id = Guid.NewGuid(), StreamType = StreamType.Audio, Index = 1, Codec = "aac" });
        }
        database.Context.MediaSources.Add(source);
        return source;
    }

    [Fact]
    public async Task Mixed_disc_and_file_is_available_but_still_matches_disc_filter()
    {
        var movie = Item("Mixed"); Source(movie, "bdmv"); Source(movie);
        await database.Context.SaveChangesAsync();
        var page = await service.ListAsync(new() { View = "bluray" }, default);
        var row = Assert.Single(page.Items);
        Assert.Equal(movie.Id, row.Id); Assert.Equal(2, row.VersionCount);
        Assert.Equal("available", row.Availability); Assert.Equal(2000, row.SizeBytes);
        Assert.Equal(1, page.Counts["attention"]);
        Assert.Equal(0, page.Counts["unavailable"]);
        var sources = await service.SourcesAsync(movie.Id, new() { View = "bluray" }, default);
        Assert.Equal(2, sources.Count); Assert.Single(sources, s => s.MatchesFilter);
    }

    [Fact]
    public async Task Technical_conditions_must_match_one_version_and_paging_counts_titles()
    {
        var mixed = Item("Mixed"); Source(mixed, height: 2160); Source(mixed, profile: 7);
        var match = Item("Matching"); Source(match, profile: 7, height: 2160); Source(match, profile: 7, height: 2160);
        await database.Context.SaveChangesAsync();
        var page = await service.ListAsync(new() { Resolution = "2160p", DvProfile = "7", Limit = 1 }, default);
        Assert.Equal(match.Id, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.Total); Assert.Equal(1, page.Counts["dolby-vision"]);
    }

    [Fact]
    public async Task Unknown_copy_prevents_definitive_unavailable_and_missing_probe_is_not_an_error()
    {
        var movie = Item("Unassessed"); Source(movie, "bdmv"); Source(movie, probe: false);
        var pending = Item("No recent index observation"); Source(pending, "mkv");
        await database.Context.SaveChangesAsync();
        var page = await service.ListAsync(new(), default);
        Assert.All(page.Items, row => Assert.Equal("unknown", row.Availability));
        Assert.Equal(0, page.Counts["unavailable"]);
        Assert.Equal(1, page.Counts["attention"]);
    }

    [Fact]
    public async Task A_missing_audio_codec_is_unknown_until_an_eligible_track_is_known()
    {
        var movie = Item("Partially probed"); Source(movie, "bdmv");
        var file = Source(movie);
        var track = file.Streams.Single(t => t.StreamType == StreamType.Audio);
        track.Codec = null;
        await database.Context.SaveChangesAsync();
        var row = Assert.Single((await service.ListAsync(new(), default)).Items);
        Assert.Equal("unknown", row.Availability);
        Assert.Equal("media_unknown", (await service.SourcesAsync(movie.Id, new(), default)).Single(s => s.Id == file.Id).Reason);

        track.Codec = "aac";
        await database.Context.SaveChangesAsync();
        Assert.Equal("available", Assert.Single((await service.ListAsync(new(), default)).Items).Availability);
    }

    [Fact]
    public async Task Series_counts_an_affected_episode_once_and_drills_into_same_sources()
    {
        var series = Item("Show", MediaKind.Series);
        var episode = Item("Pilot", MediaKind.Episode, series.Id); Source(episode, profile: 7); Source(episode, profile: 7);
        var other = Item("Second", MediaKind.Episode, series.Id); Source(other);
        await database.Context.SaveChangesAsync();
        var page = await service.ListAsync(new() { Kind = "Series", View = "dolby-vision" }, default);
        var row = Assert.Single(page.Items);
        Assert.Equal(1, row.AffectedEpisodes); Assert.Equal(2, row.EpisodeCount); Assert.Equal(3, row.VersionCount);
        var episodes = await service.ListAsync(new() { Kind = "Episode", SeriesId = series.Id, View = "dolby-vision" }, default);
        Assert.Equal(episode.Id, Assert.Single(episodes.Items).Id);
    }

    [Fact]
    public async Task Series_issue_order_uses_distinct_affected_episodes_not_version_count()
    {
        var manyVersions = Item("One affected episode", MediaKind.Series);
        var episode = Item("Pilot", MediaKind.Episode, manyVersions.Id);
        Source(episode, profile: 7); Source(episode, profile: 7); Source(episode, profile: 7);
        var manyEpisodes = Item("Two affected episodes", MediaKind.Series);
        Source(Item("First", MediaKind.Episode, manyEpisodes.Id), profile: 7);
        Source(Item("Second", MediaKind.Episode, manyEpisodes.Id), profile: 7);
        await database.Context.SaveChangesAsync();

        var page = await service.ListAsync(new(Kind: "Series", Sort: "issues", Descending: true), default);
        Assert.Equal(new[] { manyEpisodes.Id, manyVersions.Id }, page.Items.Select(row => row.Id));
        Assert.Equal(new[] { 2, 1 }, page.Items.Select(row => row.AffectedEpisodes));
    }

    [Fact]
    public async Task Cover_art_is_ignored_and_removed_items_are_excluded()
    {
        var movie = Item("Movie"); var source = Source(movie);
        source.Streams.Add(new MediaStream { Id = Guid.NewGuid(), StreamType = StreamType.Video, Codec = "mjpeg", Index = -1 });
        var removed = Item("Removed"); removed.PublicId = null; removed.RemovedAt = DateTimeOffset.UtcNow;
        await database.Context.SaveChangesAsync();
        var row = Assert.Single((await service.ListAsync(new(), default)).Items);
        Assert.Equal("available", row.Availability);
        var publicRows = await service.AvailabilityAsync([movie.Id, removed.Id], default);
        Assert.Equal(movie.Id, Assert.Single(publicRows).Id);
    }

    [Fact]
    public async Task Filters_treat_search_wildcards_literally_and_validate_windows()
    {
        Source(Item("100%")); Source(Item("1000")); await database.Context.SaveChangesAsync();
        Assert.Single((await service.ListAsync(new() { Search = "%" }, default)).Items);
        await Assert.ThrowsAsync<ManagementValidationException>(() => service.ListAsync(new() { Limit = 101 }, default));
        await Assert.ThrowsAsync<ManagementValidationException>(() => service.ListAsync(new() { Kind = "Season" }, default));
    }

    [Fact]
    public async Task Quick_counts_are_independent_and_attention_matches_the_filtered_version()
    {
        var movie = Item("Mixed"); Source(movie, "bdmv"); Source(movie, profile: 7);
        await database.Context.SaveChangesAsync();
        var page = await service.ListAsync(new(View: "bluray"), default);
        Assert.Equal(1, page.Counts["dolby-vision"]);
        var filtered = await service.ListAsync(new(View: "attention", SourceKind: "file", DvProfile: "8"), default);
        Assert.Empty(filtered.Items);
        var ordinary = Item("Ordinary alternative"); Source(ordinary, "bdmv"); Source(ordinary);
        await database.Context.SaveChangesAsync();
        var matching = await service.ListAsync(new(View: "attention", SourceKind: "file"), default);
        Assert.Equal(movie.Id, Assert.Single(matching.Items).Id);
    }

    [Fact]
    public async Task Unused_DV7_does_not_warn_viewers_and_missing_DV_profile_is_unknown()
    {
        var movie = Item("Converted"); Source(movie, profile: 7); Source(movie, profile: 8);
        var unknown = Item("Unknown DV"); var source = Source(unknown);
        source.Streams.First(t => t.StreamType == StreamType.Video).HdrFormat = "Dolby Vision";
        await database.Context.SaveChangesAsync();
        var viewer = await service.AvailabilityAsync([movie.Id, unknown.Id], default);
        Assert.False(viewer.Single(i => i.Id == movie.Id).DolbyVisionFallback);
        Assert.Equal("unknown", viewer.Single(i => i.Id == unknown.Id).Availability);
        Assert.Equal(1, (await service.ListAsync(new(ItemId: movie.Id), default)).Items.Single().IssueCount);
    }

    [Fact]
    public async Task Offline_and_unanchored_storage_override_otherwise_eligible_sources()
    {
        var movie = Item("Stored"); Source(movie); await database.Context.SaveChangesAsync();
        var hosted = new ManagementService(database.Context, new MediaServerSettings
        { CatalogMountRoots = [new("Other", "/other")] });
        Assert.Equal("blocked", Assert.Single((await hosted.ListAsync(new(), default)).Items).Availability);
        database.Context.Catalogs.Single().OfflineSince = DateTimeOffset.UtcNow;
        await database.Context.SaveChangesAsync();
        Assert.Equal("storage_unavailable", Assert.Single(await service.SourcesAsync(movie.Id, new(), default)).Reason);
    }

    [Fact]
    public async Task Stable_pages_do_not_duplicate_tied_titles_and_unknown_size_is_not_zero()
    {
        var a = Item("Same"); var b = Item("Same"); Source(a); Source(b).SizeBytes = 0;
        await database.Context.SaveChangesAsync();
        var first = await service.ListAsync(new(Limit: 1), default);
        var second = await service.ListAsync(new(Limit: 1, Offset: 1), default);
        Assert.Equal(2, first.Total); Assert.Equal(2, second.Total);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        Assert.Null(first.Items.Concat(second.Items).Single(i => i.Id == b.Id).SizeBytes);
    }

    [Fact]
    public async Task Recent_indexing_observations_distinguish_preparing_ready_and_failed()
    {
        var source = Source(Item("Indexing"), "mkv");
        await database.Context.SaveChangesAsync();
        var root = Directory.CreateTempSubdirectory("management-index");
        try
        {
            var progress = new IndexingProgress(new(root.FullName, NullLogger<RemuxIndexStore>.Instance), new SseRealtimeNotifier(), TimeProvider.System);
            var observed = new ManagementService(database.Context, new MediaServerSettings(), progress);
            foreach (var (state, availability) in new[] { ("waiting", "pending"), ("ready", "available"), ("failed", "blocked") })
            {
                progress.Observe(source.Id, state);
                Assert.Equal(availability, Assert.Single(await observed.SourcesAsync(source.MediaItemId, new(), default)).Availability);
            }
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Missing_episode_sources_need_attention_and_unavailable_counts_match_episode_drilldown()
    {
        var series = Item("Show", MediaKind.Series);
        Item("Missing", MediaKind.Episode, series.Id);
        var mixed = Item("Mixed", MediaKind.Episode, series.Id); Source(mixed, "bdmv"); Source(mixed);
        await database.Context.SaveChangesAsync();
        var attention = await service.ListAsync(new(Kind: "Series", View: "attention"), default);
        Assert.Equal(2, Assert.Single(attention.Items).AffectedEpisodes);
        var summary = await service.ListAsync(new(Kind: "Series", View: "unavailable"), default);
        Assert.Empty(summary.Items); // A series with an eligible episode is not entirely unavailable.
        var episodePage = await service.ListAsync(new(Kind: "Episode", SeriesId: series.Id, View: "unavailable"), default);
        Assert.Equal("Missing", Assert.Single(episodePage.Items).Title);
    }

    [Theory]
    [InlineData("hevc", "aac", "SDR")]
    [InlineData("av1", "aac", "SDR")]
    [InlineData("h264", "dts", "SDR")]
    [InlineData("hevc", "aac", "HLG")]
    [InlineData("hevc", "aac", "HDR10+")]
    public async Task Stored_source_reasons_match_native_negotiation(string codec, string audio, string hdr)
    {
        var source = Source(Item("Parity"));
        source.Streams.First(t => t.StreamType == StreamType.Video).Codec = codec;
        source.Streams.First(t => t.StreamType == StreamType.Video).HdrFormat = hdr;
        source.Streams.First(t => t.StreamType == StreamType.Audio).Codec = audio;
        await database.Context.SaveChangesAsync();
        var native = NativePlaybackResolver.Judge(source.Path, source.Container,
            source.Streams.Select(t => new NativePlaybackResolver.StreamFacts(t.StreamType, t.Codec, t.HdrFormat, null, false)).ToList(),
            ManagementService.Profile, RemuxReadinessState.Unsupported);
        var projected = Assert.Single(await service.SourcesAsync(source.MediaItemId, new(), default));
        Assert.Equal(native.Reason, projected.Reason);
    }

    [Fact]
    public async Task Larger_lists_still_use_bounded_SQL_queries_and_materialize_only_the_page()
    {
        for (var n = 0; n < 80; n++) { var movie = Item($"Film {n:000}"); Source(movie); Source(movie, "bdmv"); }
        await database.Context.SaveChangesAsync();
        var commands = new CommandCounter();
        await using var reads = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>()
            .UseSqlite(database.Context.Database.GetDbConnection()).AddInterceptors(commands).Options);
        var page = await new ManagementService(reads, new MediaServerSettings()).ListAsync(new(Limit: 25, Offset: 25), default);
        Assert.Equal(80, page.Total); Assert.Equal(25, page.Items.Count);
        Assert.Equal("Film 025", page.Items[0].Title);
        Assert.InRange(commands.Count, 1, 8);
        Assert.Empty(reads.ChangeTracker.Entries());
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    public void Dispose() => database.Dispose();
}
