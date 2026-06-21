using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Metadata;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Tests.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// Coverage for the operator remap (<see cref="RemapService"/>): a published leaf is reassigned to a
/// corrected identity, its canonical file is moved to match the new naming, and the orphaned old item —
/// plus any emptied season/series — is pruned.
/// </summary>
public sealed class RemapServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly RemapService _remap;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-remap-" + Guid.NewGuid().ToString("N"));

    public RemapServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        CatalogPaths.For(_root).EnsureCreated();

        var provider = new FakeMetadataProvider();
        var settings = new MediaServerSettings { SupportedLanguages = ["en-US"] };
        var sandbox = new CatalogPathSandbox();
        _remap = new RemapService(
            _database,
            new IdentifyService(_database, new NameParser(), provider, NullLogger<IdentifyService>.Instance),
            new EnrichService(_database, provider, settings),
            sandbox,
            NullLogger<RemapService>.Instance);
    }

    [Fact]
    public async Task Remap_movie_relinks_file_to_corrected_identity_and_purges_old_item()
    {
        var catalog = await SeedCatalogAsync(CatalogType.Movie);
        var (movie, oldRelative) = await SeedPublishedMovieAsync(catalog, "Wrong Title", 2000, "tmdb", "111", "payload");

        var result = await _remap.RemapAsync(movie.Id,
            new RemapRequest(MediaKind.Movie, "tmdb", "222", "Correct Movie", 2021, null, null), CancellationToken.None);

        Assert.Equal(RemapResult.Kind.Ok, result.Status);
        Assert.NotEqual(movie.Id, result.TargetId);

        // Old item is gone; the new one carries the corrected identity and a minted public id.
        Assert.False(await _database.MediaItems.AnyAsync(item => item.Id == movie.Id));
        var target = await _database.MediaItems.SingleAsync(item => item.Id == result.TargetId);
        Assert.Equal("Correct Movie", target.Title);
        Assert.Equal("222", target.IdentityProviderId);
        Assert.NotNull(target.PublicId);
        Assert.Equal("Correct Movie (2021)/Correct Movie (2021).mkv", target.LibraryPath);

        // The source row followed the file to the new item and the bytes survived the relink.
        var source = Assert.Single(await _database.MediaSources.ToListAsync());
        Assert.Equal(result.TargetId, source.MediaItemId);
        Assert.Equal(target.LibraryPath, source.Path);

        var newAbsolute = Path.Combine(_root, target.LibraryPath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(newAbsolute));
        Assert.Equal("payload", await File.ReadAllTextAsync(newAbsolute));
        Assert.False(File.Exists(Path.Combine(_root, oldRelative.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Remap_to_the_same_identity_is_a_no_op()
    {
        var catalog = await SeedCatalogAsync(CatalogType.Movie);
        var (movie, relative) = await SeedPublishedMovieAsync(catalog, "Inception", 2010, "tmdb", "27205", "bytes");

        var result = await _remap.RemapAsync(movie.Id,
            new RemapRequest(MediaKind.Movie, "tmdb", "27205", "Inception", 2010, null, null), CancellationToken.None);

        Assert.Equal(RemapResult.Kind.Ok, result.Status);
        Assert.Equal(movie.Id, result.TargetId);
        Assert.True(await _database.MediaItems.AnyAsync(item => item.Id == movie.Id));
        Assert.True(File.Exists(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Remap_episode_to_a_different_series_purges_the_emptied_old_series()
    {
        var catalog = await SeedCatalogAsync(CatalogType.Series);
        var (episode, oldRelative) = await SeedPublishedEpisodeAsync(catalog, "Old Show", "tmdb", "900", season: 1, number: 1, "ep");
        var oldSeriesId = episode.SeriesId!.Value;
        var oldSeasonId = episode.SeasonId!.Value;

        var result = await _remap.RemapAsync(episode.Id,
            new RemapRequest(MediaKind.Episode, "tmdb", "901", "New Show", 2022, 2, 5), CancellationToken.None);

        Assert.Equal(RemapResult.Kind.Ok, result.Status);

        // The old episode + its now-empty season + series are all gone.
        Assert.False(await _database.MediaItems.AnyAsync(item => item.Id == episode.Id));
        Assert.False(await _database.MediaItems.AnyAsync(item => item.Id == oldSeasonId));
        Assert.False(await _database.MediaItems.AnyAsync(item => item.Id == oldSeriesId));

        // A fresh series/season/episode exist for the corrected identity and the file moved.
        var target = await _database.MediaItems.SingleAsync(item => item.Id == result.TargetId);
        Assert.Equal(MediaKind.Episode, target.Kind);
        Assert.Equal("New Show (2022)/Season 02/New Show S02E05.mkv", target.LibraryPath);
        Assert.True(await _database.MediaItems.AnyAsync(item => item.Kind == MediaKind.Series && item.IdentityProviderId == "901"));

        var newAbsolute = Path.Combine(_root, target.LibraryPath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(newAbsolute));
        Assert.Equal("ep", await File.ReadAllTextAsync(newAbsolute));
        Assert.False(File.Exists(Path.Combine(_root, oldRelative.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Remap_rejects_a_series_container()
    {
        var catalog = await SeedCatalogAsync(CatalogType.Series);
        var (episode, _) = await SeedPublishedEpisodeAsync(catalog, "Show", "tmdb", "700", season: 1, number: 1, "x");

        var result = await _remap.RemapAsync(episode.SeriesId!.Value,
            new RemapRequest(MediaKind.Episode, "tmdb", "701", "Other", null, 1, 1), CancellationToken.None);

        Assert.Equal(RemapResult.Kind.Unsupported, result.Status);
    }

    private async Task<Catalog> SeedCatalogAsync(CatalogType type)
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = type.ToString(),
            Type = type,
            Root = _root,
            NamingTemplate = "{Title} ({Year})",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _database.Catalogs.Add(catalog);
        await _database.SaveChangesAsync();
        return catalog;
    }

    private async Task<(MediaItem Item, string LibraryRelative)> SeedPublishedMovieAsync(
        Catalog catalog, string title, int year, string provider, string providerId, string content)
    {
        var now = DateTimeOffset.UtcNow;
        var relative = $"{title} ({year})/{title} ({year}).mkv";
        var movie = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            Kind = MediaKind.Movie,
            Title = title,
            Year = year,
            IdentityProvider = provider,
            IdentityProviderId = providerId,
            Providers = new Dictionary<string, string> { [provider] = providerId },
            PublicId = PublicIdFactory.ForMovie(catalog.Id, provider, providerId),
            LibraryPath = relative,
            AddedAt = now,
            UpdatedAt = now,
        };
        _database.MediaItems.Add(movie);
        _database.MediaSources.Add(new MediaSource
        {
            Id = Guid.NewGuid(),
            MediaItemId = movie.Id,
            Container = "matroska",
            Path = relative,
            SizeBytes = content.Length,
            CreatedAt = now,
        });
        await _database.SaveChangesAsync();
        WriteFile(relative, content);
        return (movie, relative);
    }

    private async Task<(MediaItem Episode, string LibraryRelative)> SeedPublishedEpisodeAsync(
        Catalog catalog, string seriesTitle, string provider, string seriesId, int season, int number, string content)
    {
        var now = DateTimeOffset.UtcNow;
        var series = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            Kind = MediaKind.Series,
            Title = seriesTitle,
            IdentityProvider = provider,
            IdentityProviderId = seriesId,
            Providers = new Dictionary<string, string> { [provider] = seriesId },
            PublicId = PublicIdFactory.ForSeries(catalog.Id, provider, seriesId),
            AddedAt = now,
            UpdatedAt = now,
        };
        var seasonItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            Kind = MediaKind.Season,
            Title = $"Season {season}",
            ParentId = series.Id,
            SeriesId = series.Id,
            IndexNumber = season,
            ParentIndexNumber = season,
            IdentityProvider = provider,
            IdentityProviderId = seriesId,
            IdentitySeasonNumber = season,
            AddedAt = now,
            UpdatedAt = now,
        };
        var relative = $"{seriesTitle}/Season {season:D2}/{seriesTitle} S{season:D2}E{number:D2}.mkv";
        var episode = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            Kind = MediaKind.Episode,
            Title = $"Episode {number}",
            ParentId = seasonItem.Id,
            SeriesId = series.Id,
            SeasonId = seasonItem.Id,
            IndexNumber = number,
            ParentIndexNumber = season,
            IdentityProvider = provider,
            IdentityProviderId = seriesId,
            IdentitySeasonNumber = season,
            IdentityEpisodeNumber = number,
            LibraryPath = relative,
            AddedAt = now,
            UpdatedAt = now,
        };
        _database.MediaItems.AddRange(series, seasonItem, episode);
        _database.MediaSources.Add(new MediaSource
        {
            Id = Guid.NewGuid(),
            MediaItemId = episode.Id,
            Container = "matroska",
            Path = relative,
            SizeBytes = content.Length,
            CreatedAt = now,
        });
        await _database.SaveChangesAsync();
        WriteFile(relative, content);
        return (episode, relative);
    }

    private void WriteFile(string relative, string content)
    {
        var absolute = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
