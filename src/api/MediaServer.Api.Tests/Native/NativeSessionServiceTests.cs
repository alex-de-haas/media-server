using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.WatchHistory;
using Microsoft.Extensions.Logging.Abstractions;
using MediaServer.Api.Native.Playback;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// Playback reporting from a native client. The point of these is that the rows are
/// <b>indistinguishable</b> from the ones the Jellyfin path writes for the same viewing — both go
/// through <see cref="UserDataService"/>, and a second writer is how watched state would start
/// depending on which client played the file.
/// </summary>
public sealed class NativeSessionServiceTests : IDisposable
{
    private const int UserId = 21;

    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly Guid _itemId = Guid.NewGuid();
    private string _publicId = string.Empty;

    public NativeSessionServiceTests()
    {
        _context = _db.Create();

        var catalogId = Guid.NewGuid();
        _publicId = Guid.NewGuid().ToString("N");
        _context.Catalogs.Add(new Catalog
        {
            Id = catalogId, Name = "Movies", Type = CatalogType.Movie, Root = "/tmp/none",
        });
        _context.AppUsers.Add(new AppUser { Id = UserId, HostUserId = "host-21", DisplayName = "Alex" });
        _context.MediaItems.Add(new MediaItem
        {
            Id = _itemId,
            CatalogId = catalogId,
            Kind = MediaKind.Movie,
            Title = "Film",
            PublicId = _publicId,
        });
        _context.MediaSources.Add(new MediaSource
        {
            Id = Guid.NewGuid(),
            MediaItemId = _itemId,
            Path = "Film.mkv",
            Container = "mkv",
            SizeBytes = 1,
            // Two hours, so a position can be a meaningful fraction of it.
            DurationTicks = TimeSpan.FromHours(2).Ticks,
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    // Built with the recorder the composition root always injects: without it no PlaybackHistoryEntry
    // is written, and the diary and the recommendation seeds both read that table.
    private UserDataService UserData() => new(
        _context,
        TimeProvider.System,
        new WatchHistoryRecorder(
            _context,
            new WatchHistoryIdentityMapper(_context),
            TimeProvider.System,
            NullLogger<WatchHistoryRecorder>.Instance));

    private NativeSessionService Service() => new(_context, UserData());

    [Fact]
    public async Task Starting_mints_a_session_id_the_client_did_not_choose()
    {
        // The id is what keeps one viewing from counting twice when a viewer rewinds past the watched
        // threshold and watches forward again, so the server owns it.
        var id = await Service().StartAsync(
            UserId, new NativeSessionStart(_itemId, null, null, null, "tv-1"), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(id));
    }

    [Fact]
    public async Task Progress_is_stored_as_a_resume_point()
    {
        var session = await Service().StartAsync(
            UserId, new NativeSessionStart(_itemId, null, null, null, "tv-1"), CancellationToken.None);

        var halfway = TimeSpan.FromHours(1).Ticks;
        Assert.True(await Service().ReportAsync(
            UserId, new NativeSessionReport(_itemId, session, halfway), isStopped: false, CancellationToken.None));

        var row = await _context.UserItemData.AsNoTracking()
            .SingleAsync(data => data.AppUserId == UserId && data.MediaItemId == _itemId);

        Assert.Equal(halfway, row.PlaybackPositionTicks);
        Assert.False(row.Played);
    }

    [Fact]
    public async Task Watching_to_the_end_marks_it_played_exactly_as_the_jellyfin_path_would()
    {
        var session = await Service().StartAsync(
            UserId, new NativeSessionStart(_itemId, null, null, null, "tv-1"), CancellationToken.None);

        var nearlyDone = (long)(TimeSpan.FromHours(2).Ticks * 0.95);
        await Service().ReportAsync(
            UserId, new NativeSessionReport(_itemId, session, nearlyDone), isStopped: true, CancellationToken.None);

        var row = await _context.UserItemData.AsNoTracking()
            .SingleAsync(data => data.AppUserId == UserId && data.MediaItemId == _itemId);

        Assert.True(row.Played);
        Assert.Equal(1, row.PlayCount);
    }

    [Fact]
    public async Task A_play_lands_in_the_history_the_diary_and_the_recommendations_read()
    {
        var session = await Service().StartAsync(
            UserId, new NativeSessionStart(_itemId, null, null, null, "tv-1"), CancellationToken.None);

        await Service().ReportAsync(
            UserId,
            new NativeSessionReport(_itemId, session, (long)(TimeSpan.FromHours(2).Ticks * 0.95)),
            isStopped: true,
            CancellationToken.None);

        Assert.True(await _context.PlaybackHistoryEntries.AsNoTracking()
            .AnyAsync(entry => entry.MediaItemId == _itemId && entry.AppUserId == UserId));
    }

    [Fact]
    public async Task An_unpublished_item_cannot_be_reported_against()
    {
        var item = _context.MediaItems.Single(candidate => candidate.Id == _itemId);
        item.PublicId = null;
        item.RemovedAt = DateTimeOffset.UtcNow;
        _context.SaveChanges();

        Assert.Null(await Service().StartAsync(
            UserId, new NativeSessionStart(_itemId, null, null, null, "tv-1"), CancellationToken.None));

        Assert.False(await Service().ReportAsync(
            UserId, new NativeSessionReport(_itemId, "whatever", 1), isStopped: false, CancellationToken.None));
    }

    [Fact]
    public async Task A_negative_position_is_clamped_rather_than_stored()
    {
        var session = await Service().StartAsync(
            UserId, new NativeSessionStart(_itemId, null, null, null, "tv-1"), CancellationToken.None);

        await Service().ReportAsync(
            UserId, new NativeSessionReport(_itemId, session, -5_000), isStopped: false, CancellationToken.None);

        var row = await _context.UserItemData.AsNoTracking()
            .SingleAsync(data => data.AppUserId == UserId && data.MediaItemId == _itemId);

        Assert.True(row.PlaybackPositionTicks >= 0);
    }
}
