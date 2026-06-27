using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// Coverage for <see cref="LibrarySourceService"/>: pinning/clearing the default source and renaming/clearing
/// a source's version label, including the unknown-id and no-op-write paths. Uses the shared in-memory SQLite
/// fixture.
/// </summary>
public sealed class LibrarySourceServiceTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly LibrarySourceService _service;

    private readonly Guid _movieId = Guid.NewGuid();
    private readonly Guid _sourceA = Guid.NewGuid();
    private readonly Guid _sourceB = Guid.NewGuid();
    private readonly Guid _otherMovieId = Guid.NewGuid();
    private readonly Guid _otherSource = Guid.NewGuid();

    public LibrarySourceServiceTests()
    {
        Seed();
        _context = _db.Create();
        _service = new LibrarySourceService(_context);
    }

    [Fact]
    public async Task SetDefaultSource_pins_a_source_that_belongs_to_the_item()
    {
        var ok = await _service.SetDefaultSourceAsync(_movieId, _sourceB, CancellationToken.None);

        Assert.True(ok);
        await using var verify = _db.Create();
        Assert.Equal(_sourceB, (await verify.MediaItems.FindAsync(_movieId))!.DefaultSourceId);
    }

    [Fact]
    public async Task SetDefaultSource_clears_with_null()
    {
        await _service.SetDefaultSourceAsync(_movieId, _sourceA, CancellationToken.None);

        var ok = await _service.SetDefaultSourceAsync(_movieId, null, CancellationToken.None);

        Assert.True(ok);
        await using var verify = _db.Create();
        Assert.Null((await verify.MediaItems.FindAsync(_movieId))!.DefaultSourceId);
    }

    [Fact]
    public async Task SetDefaultSource_rejects_a_source_from_another_item()
    {
        var ok = await _service.SetDefaultSourceAsync(_movieId, _otherSource, CancellationToken.None);

        Assert.False(ok);
        await using var verify = _db.Create();
        Assert.Null((await verify.MediaItems.FindAsync(_movieId))!.DefaultSourceId);
    }

    [Fact]
    public async Task SetDefaultSource_returns_false_for_unknown_item()
    {
        Assert.False(await _service.SetDefaultSourceAsync(Guid.NewGuid(), _sourceA, CancellationToken.None));
    }

    [Fact]
    public async Task SetDefaultSource_is_a_noop_when_already_set()
    {
        await _service.SetDefaultSourceAsync(_movieId, _sourceA, CancellationToken.None);
        var before = (await _context.MediaItems.AsNoTracking().FirstAsync(item => item.Id == _movieId)).UpdatedAt;

        var ok = await _service.SetDefaultSourceAsync(_movieId, _sourceA, CancellationToken.None);

        Assert.True(ok);
        var after = (await _context.MediaItems.AsNoTracking().FirstAsync(item => item.Id == _movieId)).UpdatedAt;
        Assert.Equal(before, after); // unchanged value => no UpdatedAt bump
    }

    [Fact]
    public async Task SetVersion_renames_and_trims()
    {
        var ok = await _service.SetVersionAsync(_sourceA, "  Remux 1080p  ", CancellationToken.None);

        Assert.True(ok);
        await using var verify = _db.Create();
        Assert.Equal("Remux 1080p", (await verify.MediaSources.FindAsync(_sourceA))!.VersionName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetVersion_clears_to_null_for_blank_input(string? input)
    {
        await _service.SetVersionAsync(_sourceA, "Director's Cut", CancellationToken.None);

        var ok = await _service.SetVersionAsync(_sourceA, input, CancellationToken.None);

        Assert.True(ok);
        await using var verify = _db.Create();
        Assert.Null((await verify.MediaSources.FindAsync(_sourceA))!.VersionName);
    }

    [Fact]
    public async Task SetVersion_returns_false_for_unknown_source()
    {
        Assert.False(await _service.SetVersionAsync(Guid.NewGuid(), "x", CancellationToken.None));
    }

    private void Seed()
    {
        using var seed = _db.Create();
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(), Name = "Films", Type = CatalogType.Movie, Root = "/films",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var movie = new MediaItem
        {
            Id = _movieId, PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id, Kind = MediaKind.Movie,
            Title = "The Rock", Year = 1996, AddedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var other = new MediaItem
        {
            Id = _otherMovieId, PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id, Kind = MediaKind.Movie,
            Title = "Heat", Year = 1995, AddedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };

        seed.Catalogs.Add(catalog);
        seed.MediaItems.AddRange(movie, other);
        seed.MediaSources.AddRange(
            new MediaSource { Id = _sourceA, MediaItemId = _movieId, Container = "mkv", Path = "The Rock (1996)/The Rock (1996).mkv", CreatedAt = DateTimeOffset.UtcNow },
            new MediaSource { Id = _sourceB, MediaItemId = _movieId, Container = "mkv", Path = "The Rock (1996)/The Rock (1996) - Remux.mkv", VersionName = "Remux", CreatedAt = DateTimeOffset.UtcNow },
            new MediaSource { Id = _otherSource, MediaItemId = _otherMovieId, Container = "mkv", Path = "Heat (1995)/Heat (1995).mkv", CreatedAt = DateTimeOffset.UtcNow });
        seed.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }
}
