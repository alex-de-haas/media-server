using MediaServer.Api.Data;
using MediaServer.Api.Native.Playback;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// Storing and scoping track preferences: a title's own override, else the user's default — and the
/// change-log rows that carry the choice to the viewer's other devices.
/// </summary>
public sealed class NativePreferenceServiceTests : IDisposable
{
    private const int UserId = 11;

    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly Guid _seriesId = Guid.NewGuid();

    public NativePreferenceServiceTests()
    {
        _context = _db.Create();

        var catalogId = Guid.NewGuid();
        _context.Catalogs.Add(new Catalog
        {
            Id = catalogId, Name = "TV", Type = CatalogType.Series, Root = "/tmp/none",
        });
        _context.AppUsers.Add(new AppUser { Id = UserId, HostUserId = "host-11", DisplayName = "Alex" });
        _context.MediaItems.Add(new MediaItem
        {
            Id = _seriesId,
            CatalogId = catalogId,
            Kind = MediaKind.Series,
            Title = "A Show",
            PublicId = Guid.NewGuid().ToString("N"),
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private NativePreferenceService Service() => new(_context);

    [Fact]
    public async Task A_titles_override_wins_over_the_default()
    {
        await Service().SetAsync(UserId, new NativePreferenceDto(null, "rus", null, false, false), CancellationToken.None);
        await Service().SetAsync(
            UserId, new NativePreferenceDto(_seriesId, "eng", "rus", false, true), CancellationToken.None);

        var forShow = await Service().ResolveAsync(UserId, _seriesId, CancellationToken.None);
        Assert.Equal("eng", forShow!.AudioLanguage);
        Assert.True(forShow.PreferOriginalAudio);

        var forSomethingElse = await Service().ResolveAsync(UserId, Guid.NewGuid(), CancellationToken.None);
        Assert.Equal("rus", forSomethingElse!.AudioLanguage);
    }

    [Fact]
    public async Task Clearing_an_override_falls_back_to_the_default()
    {
        await Service().SetAsync(UserId, new NativePreferenceDto(null, "rus", null, false, false), CancellationToken.None);
        await Service().SetAsync(UserId, new NativePreferenceDto(_seriesId, "eng", null, false, false), CancellationToken.None);

        Assert.True(await Service().ClearAsync(UserId, _seriesId, CancellationToken.None));

        var resolved = await Service().ResolveAsync(UserId, _seriesId, CancellationToken.None);
        Assert.Equal("rus", resolved!.AudioLanguage);
    }

    [Fact]
    public async Task Setting_the_same_scope_twice_updates_rather_than_duplicating()
    {
        // The unique index is what makes "which one wins" a question nobody has to answer.
        await Service().SetAsync(UserId, new NativePreferenceDto(_seriesId, "rus", null, false, false), CancellationToken.None);
        await Service().SetAsync(UserId, new NativePreferenceDto(_seriesId, "eng", null, false, false), CancellationToken.None);

        var rows = await _context.PlaybackPreferences.AsNoTracking()
            .Where(row => row.AppUserId == UserId)
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal("eng", rows[0].AudioLanguage);
    }

    [Fact]
    public async Task A_choice_reaches_the_change_log_so_it_syncs_to_the_other_devices()
    {
        await Service().SetAsync(UserId, new NativePreferenceDto(_seriesId, "rus", null, false, false), CancellationToken.None);

        var row = await _context.ChangeLog.AsNoTracking()
            .SingleAsync(entry => entry.EntityType == ChangeEntityType.PlaybackPreference);

        Assert.Equal(UserId, row.AppUserId);
        Assert.Equal(_seriesId.ToString("N"), row.EntityId);
        Assert.Equal(ChangeKind.Upsert, row.Kind);
    }

    [Fact]
    public async Task Clearing_is_recorded_as_a_delete()
    {
        await Service().SetAsync(UserId, new NativePreferenceDto(null, "rus", null, false, false), CancellationToken.None);
        await Service().ClearAsync(UserId, null, CancellationToken.None);

        var deletes = await _context.ChangeLog.AsNoTracking()
            .Where(entry => entry.EntityType == ChangeEntityType.PlaybackPreference && entry.Kind == ChangeKind.Delete)
            .ToListAsync();

        Assert.Single(deletes);
        Assert.Equal("global", deletes[0].EntityId);
    }

    [Fact]
    public async Task A_scope_naming_a_title_that_does_not_exist_is_refused()
    {
        // Left to the foreign key this would be either a 500 from the constraint or a preference
        // stored against nothing; neither is an answer.
        var saved = await Service().SetAsync(
            UserId, new NativePreferenceDto(Guid.NewGuid(), "rus", null, false, false), CancellationToken.None);

        Assert.Null(saved);
        Assert.Empty(await _context.PlaybackPreferences.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_scope_naming_a_tombstoned_title_is_refused_like_everywhere_else()
    {
        var series = _context.MediaItems.Single(item => item.Id == _seriesId);
        series.PublicId = null;
        series.RemovedAt = DateTimeOffset.UtcNow;
        _context.SaveChanges();

        Assert.Null(await Service().SetAsync(
            UserId, new NativePreferenceDto(_seriesId, "rus", null, false, false), CancellationToken.None));
    }

    [Fact]
    public async Task Blank_languages_are_stored_as_absent_rather_than_as_empty_strings()
    {
        var saved = await Service().SetAsync(
            UserId, new NativePreferenceDto(null, "  ", "", false, false), CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Null(saved!.AudioLanguage);
        Assert.Null(saved.SubtitleLanguage);
    }
}
