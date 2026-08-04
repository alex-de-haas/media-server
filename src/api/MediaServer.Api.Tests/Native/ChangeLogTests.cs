using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// The change log is what <c>/native/v1/sync</c> paginates over, so a mutation that fails to leave a
/// row is a row that silently stops reaching every client. These pin the two ways a write reaches the
/// database — through the change tracker, and around it.
/// </summary>
public sealed class ChangeLogTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;

    public ChangeLogTests() => _context = _db.Create();

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private async Task<Guid> SeedCatalogAsync(string root)
    {
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = root,
        };
        _context.Catalogs.Add(catalog);
        await _context.SaveChangesAsync();
        return catalog.Id;
    }

    private async Task<MediaItem> SeedMovieAsync(Guid catalogId, string title = "A Film")
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalogId,
            Kind = MediaKind.Movie,
            Title = title,
            PublicId = Guid.NewGuid().ToString("N"),
        };
        _context.MediaItems.Add(item);
        await _context.SaveChangesAsync();
        return item;
    }

    [Fact]
    public async Task Records_an_upsert_when_an_item_is_added_and_when_it_changes()
    {
        var catalogId = await SeedCatalogAsync("/tmp/none");
        var item = await SeedMovieAsync(catalogId);

        var added = await _context.ChangeLog.AsNoTracking()
            .Where(entry => entry.EntityType == ChangeEntityType.MediaItem)
            .ToListAsync();
        Assert.Single(added);
        Assert.Equal(ChangeKind.Upsert, added[0].Kind);
        Assert.Equal(item.Id.ToString("N"), added[0].EntityId);

        item.Title = "A Film (Director's Cut)";
        await _context.SaveChangesAsync();

        var afterEdit = await _context.ChangeLog.AsNoTracking()
            .CountAsync(entry => entry.EntityType == ChangeEntityType.MediaItem);
        Assert.Equal(2, afterEdit);
    }

    [Fact]
    public async Task Carries_the_user_on_a_per_user_change_so_one_feed_never_leaks_into_another()
    {
        var catalogId = await SeedCatalogAsync("/tmp/none");
        var item = await SeedMovieAsync(catalogId);

        _context.AppUsers.Add(new AppUser { Id = 42, HostUserId = "host-42", DisplayName = "Alex" });
        await _context.SaveChangesAsync();

        _context.UserItemData.Add(new UserItemData
        {
            AppUserId = 42,
            MediaItemId = item.Id,
            Played = true,
        });
        await _context.SaveChangesAsync();

        var row = await _context.ChangeLog.AsNoTracking()
            .SingleAsync(entry => entry.EntityType == ChangeEntityType.UserItemData);

        Assert.Equal(42, row.AppUserId);
        Assert.Equal(item.Id.ToString("N"), row.EntityId);
    }

    [Fact]
    public async Task Records_a_delete_when_a_purge_removes_an_item_the_tracker_never_sees()
    {
        // The whole reason the log exists: a purged item leaves no tombstone to poll, and the delete
        // runs through ExecuteDelete, which the DbContext hook cannot observe.
        var root = Path.Combine(Path.GetTempPath(), "ms-changelog-" + Guid.NewGuid().ToString("N"));
        CatalogPaths.For(root).EnsureCreated();
        try
        {
            var catalogId = await SeedCatalogAsync(root);
            var item = await SeedMovieAsync(catalogId);

            var service = new LibraryDeleteService(
                _context,
                new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));
            await service.DeleteAsync(item.Id, deleteFiles: false, deleteUserData: false, CancellationToken.None);

            Assert.False(await _context.MediaItems.AsNoTracking().AnyAsync(media => media.Id == item.Id));

            var deletes = await _context.ChangeLog.AsNoTracking()
                .Where(entry => entry.Kind == ChangeKind.Delete && entry.EntityId == item.Id.ToString("N"))
                .ToListAsync();
            Assert.Single(deletes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Sequence_is_monotonic_and_never_reused_after_the_log_is_pruned_empty()
    {
        // Retention pruning deletes old rows; if SQLite reused the freed rowids, a client holding a
        // cursor past that point would never see anything again. AUTOINCREMENT is what forbids reuse,
        // and this asserts the schema actually carries it.
        var catalogId = await SeedCatalogAsync("/tmp/none");
        await SeedMovieAsync(catalogId, "First");
        await SeedMovieAsync(catalogId, "Second");

        var highest = await _context.ChangeLog.AsNoTracking().MaxAsync(entry => entry.Sequence);

        await _context.ChangeLog.ExecuteDeleteAsync();
        Assert.False(await _context.ChangeLog.AsNoTracking().AnyAsync());

        await SeedMovieAsync(catalogId, "Third");

        var next = await _context.ChangeLog.AsNoTracking().MinAsync(entry => entry.Sequence);
        Assert.True(next > highest, $"sequence {next} must not reuse a value at or below {highest}");
    }
}
