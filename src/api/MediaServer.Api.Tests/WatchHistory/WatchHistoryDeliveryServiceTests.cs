using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.WatchHistory;

public sealed class WatchHistoryDeliveryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-23T12:00:00Z"));
    private readonly FakeProvider _provider = new();
    private readonly int _userId;
    private readonly Guid _itemId;
    private readonly Guid _connectionId;

    public WatchHistoryDeliveryServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        var user = new AppUser
        {
            HostUserId = "host-1", Email = "alex@example.com", DisplayName = "Alex",
            Role = AppUserRole.User, CreatedAt = _time.GetUtcNow(), LastSeenAt = _time.GetUtcNow(),
        };
        _database.AppUsers.Add(user);
        // Saved first: the identity id is assigned by the database, and the connection below needs it.
        _database.SaveChanges();

        var catalog = new Catalog
        {
            Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = "/m",
            CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.Catalogs.Add(catalog);

        var item = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id,
            Kind = MediaKind.Movie, Title = "Inception",
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(item);

        var connection = new WatchHistoryProviderConnection
        {
            Id = Guid.NewGuid(), AppUserId = user.Id, ProviderKey = "fake",
            Status = WatchHistoryConnectionStatus.Connected, ConnectedAt = _time.GetUtcNow(),
        };
        connection.SecretKey = "fake.connection.x.tokens";
        _database.WatchHistoryConnections.Add(connection);
        _database.SaveChanges();

        _userId = user.Id;
        _itemId = item.Id;
        _connectionId = connection.Id;
    }

    /// <summary>A provider whose history is a list this test can inspect and pre-seed.</summary>
    private sealed class FakeProvider : IWatchHistoryProvider
    {
        private long _nextId = 900;

        public List<WatchHistoryPlay> History { get; } = [];

        public List<string> Removed { get; } = [];

        public List<string> Calls { get; } = [];

        public WatchHistoryFailure? FailWith { get; set; }

        public TimeSpan? RetryAfter { get; set; }

        /// <summary>Simulates a provider that has not surfaced a just-written entry yet.</summary>
        public bool HideNextWrite { get; set; }

        public WatchHistoryCapabilities Overrides { get; set; } = new()
        {
            ExactTimestampWrites = true, TimelessWrites = true, AggregateWatchedReads = false,
            FullHistoryReads = true, IndividualEntryRemoval = true,
        };

        public string Key => "fake";

        public string DisplayName => "Fake";

        public WatchHistoryCapabilities Capabilities => Overrides;

        public Task<WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>> GetHistoryAsync(
            int appUserId, IReadOnlyCollection<WatchHistoryIdentity> identities, CancellationToken cancellationToken)
        {
            Calls.Add("get");
            return Task.FromResult(FailWith is { } failure
                ? WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Failed(failure, "stub", RetryAfter)
                : WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Success([.. History]));
        }

        public Task<WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>> AddPlaysAsync(
            int appUserId, IReadOnlyCollection<WatchHistoryPlay> plays, CancellationToken cancellationToken)
        {
            Calls.Add("add");
            if (FailWith is { } failure)
            {
                return Task.FromResult(WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Failed(failure, "stub", RetryAfter));
            }

            foreach (var play in plays)
            {
                if (!HideNextWrite)
                {
                    History.Add(play with { RemoteId = (_nextId++).ToString() });
                }
            }

            HideNextWrite = false;
            return Task.FromResult(WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Success([.. plays]));
        }

        public Task<WatchHistoryResult<int>> RemoveEntriesAsync(
            int appUserId, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken)
        {
            Calls.Add("remove");
            if (FailWith is { } failure)
            {
                return Task.FromResult(WatchHistoryResult<int>.Failed(failure, "stub", RetryAfter));
            }

            Removed.AddRange(remoteIds);
            History.RemoveAll(play => remoteIds.Contains(play.RemoteId));
            return Task.FromResult(WatchHistoryResult<int>.Success(remoteIds.Count));
        }
    }

    private sealed class StubRegistry(IWatchHistoryProvider provider) : IWatchHistoryProviderRegistry
    {
        public IReadOnlyList<WatchHistoryProviderDescriptor> Describe() => [];

        public IWatchHistoryProvider? Find(string providerKey) =>
            string.Equals(providerKey, provider.Key, StringComparison.OrdinalIgnoreCase) ? provider : null;

        public IWatchHistoryProviderAuthorization? FindAuthorization(string providerKey) => null;
    }

    private WatchHistoryDeliveryService Service() => new(
        _database, new StubRegistry(_provider), _time, NullLogger<WatchHistoryDeliveryService>.Instance);

    private static WatchHistoryIdentity Identity() =>
        new() { Kind = WatchHistoryMediaKind.Movie, TmdbId = 27205 };

    private WatchHistoryOutboxEvent Queue(
        WatchHistoryOutboxOperation operation, DateTimeOffset? occurredAt = null, Guid? historyEntryId = null)
    {
        var item = new WatchHistoryOutboxEvent
        {
            Id = Guid.NewGuid(),
            ConnectionId = _connectionId,
            AppUserId = _userId,
            MediaItemId = _itemId,
            HistoryEntryId = historyEntryId,
            Operation = operation,
            IdentitySnapshot = JsonSerializer.Serialize(Identity()),
            OccurredAt = occurredAt,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Status = WatchHistoryOutboxStatus.Pending,
            CreatedAt = _time.GetUtcNow(),
            NextAttemptAt = _time.GetUtcNow(),
        };
        _database.WatchHistoryOutboxEvents.Add(item);
        _database.SaveChanges();
        return item;
    }

    private PlaybackHistoryEntry AddLocalEntry(
        DateTimeOffset? watchedAt, PlaybackHistoryOrigin origin, string? remoteId = null, bool owned = false)
    {
        var entry = new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = _itemId,
            CreatedAt = _time.GetUtcNow(), WatchedAt = watchedAt, Origin = origin,
            ProviderKey = remoteId is null ? null : "fake",
            ProviderHistoryId = remoteId,
            ProviderEntryOwned = owned,
            LinkStatus = remoteId is null ? PlaybackHistoryLinkStatus.None : PlaybackHistoryLinkStatus.Resolved,
        };
        _database.PlaybackHistoryEntries.Add(entry);
        _database.SaveChanges();
        return entry;
    }

    // ---- Exact writes ----

    [Fact]
    public async Task AnExactWatchIsDeliveredAndCompleted()
    {
        var queued = Queue(WatchHistoryOutboxOperation.AddExactWatch, _time.GetUtcNow());

        var result = await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(1, result.Delivered);
        Assert.Equal(WatchHistoryOutboxStatus.Completed, (await Reload(queued)).Status);
        Assert.Single(_provider.History);
    }

    // ---- Ensure timeless: read before, write, read after ----

    [Fact]
    public async Task AnEnsureAddsNothingWhenTheProviderAlreadyHasHistory()
    {
        // Adding a second mark would put an extra viewing on the user's profile for a toggle.
        _provider.History.Add(new WatchHistoryPlay(Identity(), _time.GetUtcNow().AddDays(-2), "500"));
        var queued = Queue(WatchHistoryOutboxOperation.EnsureTimelessWatched);

        await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(WatchHistoryOutboxStatus.Completed, (await Reload(queued)).Status);
        Assert.Single(_provider.History);
        Assert.DoesNotContain("add", _provider.Calls);
    }

    [Fact]
    public async Task AnEnsureAddsOneMarkAndRecordsWhichEntryItCreated()
    {
        // Ownership is the only thing that will permit removing it later.
        var entry = AddLocalEntry(null, PlaybackHistoryOrigin.Manual);
        Queue(WatchHistoryOutboxOperation.EnsureTimelessWatched, historyEntryId: entry.Id);

        await Service().DeliverAsync(CancellationToken.None);

        var linked = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync(row => row.Id == entry.Id);
        Assert.Equal(PlaybackHistoryLinkStatus.Resolved, linked.LinkStatus);
        Assert.True(linked.ProviderEntryOwned);
        Assert.Equal(_provider.History.Single().RemoteId, linked.ProviderHistoryId);
    }

    [Fact]
    public async Task AnEntryTheProviderHasNotSurfacedYetIsLeftUnowned()
    {
        // Guessing which entry is ours would license deleting one this app did not create.
        var entry = AddLocalEntry(null, PlaybackHistoryOrigin.Manual);
        _provider.HideNextWrite = true;
        Queue(WatchHistoryOutboxOperation.EnsureTimelessWatched, historyEntryId: entry.Id);

        await Service().DeliverAsync(CancellationToken.None);

        var linked = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync(row => row.Id == entry.Id);
        Assert.Equal(PlaybackHistoryLinkStatus.Unresolved, linked.LinkStatus);
        Assert.False(linked.ProviderEntryOwned);
    }

    [Fact]
    public async Task AConcurrentWriteThatMuddiesTheDifferenceLeavesItUnowned()
    {
        var entry = AddLocalEntry(null, PlaybackHistoryOrigin.Manual);
        var queued = Queue(WatchHistoryOutboxOperation.EnsureTimelessWatched, historyEntryId: entry.Id);
        // Two new ids appear between the reads; neither can be claimed as ours.
        queued.PreCreateRemoteIds = JsonSerializer.Serialize(Array.Empty<string>());
        _provider.History.Add(new WatchHistoryPlay(Identity(), null, "701"));
        _provider.History.Add(new WatchHistoryPlay(Identity(), null, "702"));
        await _database.SaveChangesAsync();

        await Service().DeliverAsync(CancellationToken.None);

        var linked = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync(row => row.Id == entry.Id);
        Assert.Equal(PlaybackHistoryLinkStatus.Unresolved, linked.LinkStatus);
    }

    [Fact]
    public async Task ARetryAfterACommittedWriteResolvesOwnershipInsteadOfAddingAgain()
    {
        // The whole point of persisting the pre-create set: a crash between the write and the second
        // read must not add a second mark on the next pass.
        var entry = AddLocalEntry(null, PlaybackHistoryOrigin.Manual);
        var queued = Queue(WatchHistoryOutboxOperation.EnsureTimelessWatched, historyEntryId: entry.Id);
        queued.PreCreateRemoteIds = JsonSerializer.Serialize(Array.Empty<string>());
        _provider.History.Add(new WatchHistoryPlay(Identity(), null, "801"));
        await _database.SaveChangesAsync();

        await Service().DeliverAsync(CancellationToken.None);

        Assert.DoesNotContain("add", _provider.Calls);
        Assert.Single(_provider.History);
        var linked = await _database.PlaybackHistoryEntries.AsNoTracking().SingleAsync(row => row.Id == entry.Id);
        Assert.Equal("801", linked.ProviderHistoryId);
    }

    [Fact]
    public async Task AnUndoneMarkLeavesTheRemoteEntryRatherThanGuessing()
    {
        // The local entry is gone, so nothing can own the remote one; deleting it might remove a mark
        // that was never ours.
        Queue(WatchHistoryOutboxOperation.EnsureTimelessWatched, historyEntryId: Guid.NewGuid());

        var result = await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(1, result.Delivered);
        Assert.Single(_provider.History);
    }

    // ---- Owned-only removal ----

    [Fact]
    public async Task RemovalTouchesOnlyTheEntriesThisAppOwns()
    {
        AddLocalEntry(null, PlaybackHistoryOrigin.Manual, remoteId: "111", owned: true);
        AddLocalEntry(null, PlaybackHistoryOrigin.ProviderSync, remoteId: "222", owned: false);
        _provider.History.Add(new WatchHistoryPlay(Identity(), null, "111"));
        _provider.History.Add(new WatchHistoryPlay(Identity(), null, "222"));
        Queue(WatchHistoryOutboxOperation.RemoveOwnedTimelessEntries);

        await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(["111"], _provider.Removed);
        Assert.Equal("222", _provider.History.Single().RemoteId);
    }

    [Fact]
    public async Task RemovalWithNothingOwnedCompletesWithoutCallingTheProvider()
    {
        // Retrying would never find anything to remove.
        AddLocalEntry(null, PlaybackHistoryOrigin.ProviderSync, remoteId: "222", owned: false);
        var queued = Queue(WatchHistoryOutboxOperation.RemoveOwnedTimelessEntries);

        await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(WatchHistoryOutboxStatus.Completed, (await Reload(queued)).Status);
        Assert.Empty(_provider.Removed);
    }

    [Fact]
    public async Task AProviderThatCannotRemoveOneEntryIsNotAskedToRemoveEverything()
    {
        AddLocalEntry(null, PlaybackHistoryOrigin.Manual, remoteId: "111", owned: true);
        _provider.Overrides = _provider.Overrides with { IndividualEntryRemoval = false };
        var queued = Queue(WatchHistoryOutboxOperation.RemoveOwnedTimelessEntries);

        await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(WatchHistoryOutboxStatus.Terminal, (await Reload(queued)).Status);
        Assert.Empty(_provider.Removed);
    }

    // ---- Retries and terminal failures ----

    [Fact]
    public async Task ATransientFailureIsRescheduledRatherThanFailed()
    {
        _provider.FailWith = WatchHistoryFailure.Transient;
        var queued = Queue(WatchHistoryOutboxOperation.AddExactWatch, _time.GetUtcNow());

        var result = await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(1, result.Retried);
        var reloaded = await Reload(queued);
        Assert.Equal(WatchHistoryOutboxStatus.Pending, reloaded.Status);
        Assert.True(reloaded.NextAttemptAt > _time.GetUtcNow());
        Assert.Null(reloaded.LeaseUntil);
    }

    [Fact]
    public async Task ARateLimitHonoursTheProvidersOwnDelay()
    {
        _provider.FailWith = WatchHistoryFailure.RateLimited;
        _provider.RetryAfter = TimeSpan.FromMinutes(30);
        var queued = Queue(WatchHistoryOutboxOperation.AddExactWatch, _time.GetUtcNow());

        await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(_time.GetUtcNow().AddMinutes(30), (await Reload(queued)).NextAttemptAt);
    }

    [Theory]
    [InlineData(WatchHistoryFailure.AuthenticationRequired)]
    [InlineData(WatchHistoryFailure.IdentityRejected)]
    [InlineData(WatchHistoryFailure.Unsupported)]
    public async Task ATerminalFailureStopsRetrying(WatchHistoryFailure failure)
    {
        // Retrying a rejected identity only burns rate limit, and an expired credential needs the
        // user rather than another attempt.
        _provider.FailWith = failure;
        var queued = Queue(WatchHistoryOutboxOperation.AddExactWatch, _time.GetUtcNow());

        var result = await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(WatchHistoryOutboxStatus.Terminal, (await Reload(queued)).Status);
    }

    [Fact]
    public async Task WorkGivesUpAfterTheAttemptCeiling()
    {
        _provider.FailWith = WatchHistoryFailure.Transient;
        var queued = Queue(WatchHistoryOutboxOperation.AddExactWatch, _time.GetUtcNow());
        queued.Attempts = WatchHistoryDeliveryService.MaxAttempts - 1;
        await _database.SaveChangesAsync();

        await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(WatchHistoryOutboxStatus.Terminal, (await Reload(queued)).Status);
    }

    [Fact]
    public async Task AnUnreadableSnapshotIsTerminalRatherThanRetried()
    {
        // The snapshot is frozen at enqueue time precisely so delivery does not depend on the
        // library's current state; an unreadable one is a bug, not a transient condition.
        var queued = Queue(WatchHistoryOutboxOperation.AddExactWatch, _time.GetUtcNow());
        queued.IdentitySnapshot = "{not json";
        await _database.SaveChangesAsync();

        await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(WatchHistoryOutboxStatus.Terminal, (await Reload(queued)).Status);
    }

    // ---- Leasing ----

    [Fact]
    public async Task WorkNotYetDueIsLeftAlone()
    {
        var queued = Queue(WatchHistoryOutboxOperation.AddExactWatch, _time.GetUtcNow());
        queued.NextAttemptAt = _time.GetUtcNow().AddMinutes(10);
        await _database.SaveChangesAsync();

        var result = await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(0, result.Delivered);
        Assert.Empty(_provider.Calls);
    }

    [Fact]
    public async Task AnExpiredLeaseIsPickedUpAgain()
    {
        // A pass that died mid-delivery must not wedge its work forever.
        var queued = Queue(WatchHistoryOutboxOperation.AddExactWatch, _time.GetUtcNow());
        queued.Status = WatchHistoryOutboxStatus.Leased;
        queued.LeaseUntil = _time.GetUtcNow().AddMinutes(-1);
        await _database.SaveChangesAsync();

        var result = await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(1, result.Delivered);
    }

    [Fact]
    public async Task ALiveLeaseIsNotStolen()
    {
        var queued = Queue(WatchHistoryOutboxOperation.AddExactWatch, _time.GetUtcNow());
        queued.Status = WatchHistoryOutboxStatus.Leased;
        queued.LeaseUntil = _time.GetUtcNow().AddMinutes(4);
        await _database.SaveChangesAsync();

        var result = await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(0, result.Delivered);
        Assert.Empty(_provider.Calls);
    }

    [Fact]
    public async Task CompletedWorkIsNotDeliveredTwice()
    {
        Queue(WatchHistoryOutboxOperation.AddExactWatch, _time.GetUtcNow());

        await Service().DeliverAsync(CancellationToken.None);
        var result = await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(0, result.Delivered);
        Assert.Single(_provider.History);
    }

    private async Task<WatchHistoryOutboxEvent> Reload(WatchHistoryOutboxEvent item)
    {
        _database.ChangeTracker.Clear();
        return await _database.WatchHistoryOutboxEvents.AsNoTracking().SingleAsync(row => row.Id == item.Id);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
