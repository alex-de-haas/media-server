using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Native;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// The sync stream: a bounded snapshot, then the change log, and an honest answer when a client has
/// been away longer than the log remembers.
/// </summary>
public sealed class NativeSyncServiceTests : IDisposable
{
    private const int UserId = 7;

    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private Guid _catalogId;

    public NativeSyncServiceTests()
    {
        _context = _db.Create();
        Seed();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private NativeSyncService Service() =>
        new(_context, new LibraryReadService(
            _context,
            new UserDataService(_context, TimeProvider.System),
            new MediaServerSettings { SupportedLanguages = ["en-US"] }));

    private void Seed()
    {
        _catalogId = Guid.NewGuid();
        _context.Catalogs.Add(new Catalog
        {
            Id = _catalogId,
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = "/tmp/none",
        });
        _context.AppUsers.Add(new AppUser { Id = UserId, HostUserId = "host-7", DisplayName = "Alex" });
        _context.SaveChanges();
    }

    private MediaItem AddMovie(string title)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = _catalogId,
            Kind = MediaKind.Movie,
            Title = title,
            PublicId = Guid.NewGuid().ToString("N"),
        };
        _context.MediaItems.Add(item);
        _context.SaveChanges();
        return item;
    }

    [Fact]
    public async Task First_sync_returns_the_library_and_a_cursor_that_then_goes_quiet()
    {
        AddMovie("One");
        AddMovie("Two");

        var first = await Service().SyncAsync(cursor: null, UserId, CancellationToken.None);

        Assert.Equal(2, first.Items.Count);
        Assert.False(first.ResetRequired);
        Assert.False(first.HasMore);

        // Nothing changed since, so the follow-up is empty rather than a repeat of the snapshot.
        var second = await Service().SyncAsync(first.Cursor, UserId, CancellationToken.None);
        Assert.Empty(second.Items);
        Assert.Empty(second.RemovedIds);
    }

    [Fact]
    public async Task A_change_after_the_snapshot_arrives_on_the_next_page()
    {
        AddMovie("One");
        var first = await Service().SyncAsync(cursor: null, UserId, CancellationToken.None);

        var added = AddMovie("Two");

        var next = await Service().SyncAsync(first.Cursor, UserId, CancellationToken.None);

        Assert.Equal(added.Id, Assert.Single(next.Items).Id);
    }

    [Fact]
    public async Task An_item_that_stops_being_published_arrives_as_a_removal()
    {
        var item = AddMovie("One");
        var first = await Service().SyncAsync(cursor: null, UserId, CancellationToken.None);

        // Whether it was tombstoned, purged or unpublished, the client is told the same thing: it is
        // gone. Here it is tombstoned, which is the case a published-item query alone would miss.
        item.PublicId = null;
        item.RemovedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        var next = await Service().SyncAsync(first.Cursor, UserId, CancellationToken.None);

        Assert.Empty(next.Items);
        Assert.Equal(item.Id.ToString("N"), Assert.Single(next.RemovedIds));
    }

    [Fact]
    public async Task One_users_playback_never_shows_up_in_anothers_feed()
    {
        var item = AddMovie("One");
        _context.AppUsers.Add(new AppUser { Id = 99, HostUserId = "host-99", DisplayName = "Someone else" });
        await _context.SaveChangesAsync();

        var mine = await Service().SyncAsync(cursor: null, UserId, CancellationToken.None);

        _context.UserItemData.Add(new UserItemData { AppUserId = 99, MediaItemId = item.Id, Played = true });
        await _context.SaveChangesAsync();

        var next = await Service().SyncAsync(mine.Cursor, UserId, CancellationToken.None);

        Assert.Empty(next.Items);
        Assert.Empty(next.RemovedIds);
    }

    [Fact]
    public async Task My_own_playback_comes_back_so_the_mirror_can_update_it()
    {
        var item = AddMovie("One");
        var first = await Service().SyncAsync(cursor: null, UserId, CancellationToken.None);

        _context.UserItemData.Add(new UserItemData { AppUserId = UserId, MediaItemId = item.Id, Played = true });
        await _context.SaveChangesAsync();

        var next = await Service().SyncAsync(first.Cursor, UserId, CancellationToken.None);

        Assert.Equal(item.Id, Assert.Single(next.Items).Id);
    }

    [Fact]
    public async Task A_cursor_older_than_the_log_asks_for_a_reset_instead_of_lying()
    {
        AddMovie("One");
        var first = await Service().SyncAsync(cursor: null, UserId, CancellationToken.None);

        // Everything the client would have needed is pruned away, and the newest row is kept — which
        // is exactly what makes the gap detectable.
        AddMovie("Two");
        AddMovie("Three");
        await ChangeLogPruner.PruneAsync(
            _context, DateTimeOffset.UtcNow.Add(ChangeLogPruner.Retention).AddDays(1), CancellationToken.None);

        var next = await Service().SyncAsync(first.Cursor, UserId, CancellationToken.None);

        Assert.True(next.ResetRequired);
        Assert.Empty(next.Items);

        // And the cursor it hands back is usable: the client re-snapshots and is whole again.
        var resnapshot = await Service().SyncAsync(next.Cursor, UserId, CancellationToken.None);
        Assert.False(resnapshot.ResetRequired);
        Assert.Equal(3, resnapshot.Items.Count);
    }

    [Fact]
    public async Task Pruning_never_removes_the_newest_row()
    {
        AddMovie("One");

        var removed = await ChangeLogPruner.PruneAsync(
            _context, DateTimeOffset.UtcNow.Add(ChangeLogPruner.Retention).AddDays(1), CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.Equal(1, await _context.ChangeLog.CountAsync());
    }

    [Fact]
    public async Task An_unreadable_cursor_starts_over_rather_than_failing()
    {
        AddMovie("One");

        var page = await Service().SyncAsync("not-a-cursor", UserId, CancellationToken.None);

        Assert.Single(page.Items);
        Assert.False(page.ResetRequired);
    }
}
