using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Watchlist;

/// <summary>
/// Shared release-tracking test doubles: a programmable schedule provider (the mocked TMDb client), a
/// fixed clock with an explicit app timezone, and seeding helpers over a shared in-memory SQLite database.
/// </summary>
internal static class WatchlistTestData
{
    public static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return connection;
    }

    public static MediaServerDbContext NewContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(connection).Options);

    public static AppUser SeedUser(MediaServerDbContext database, string hostUserId = "host-1")
    {
        var user = new AppUser
        {
            HostUserId = hostUserId,
            Email = $"{hostUserId}@example.com",
            DisplayName = hostUserId,
            Role = AppUserRole.User,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        database.AppUsers.Add(user);
        database.SaveChanges();
        return user;
    }

    public static TrackedTitle SeedTitle(
        MediaServerDbContext database,
        MediaKind kind,
        string providerId,
        string title = "Title",
        string? status = null,
        DateTimeOffset? lastRefreshedAt = null)
    {
        var tracked = new TrackedTitle
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            IdentityProvider = "tmdb",
            IdentityProviderId = providerId,
            Providers = new Dictionary<string, string> { ["tmdb"] = providerId },
            Title = title,
            ProductionStatus = status,
            LastRefreshedAt = lastRefreshedAt,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        database.TrackedTitles.Add(tracked);
        database.SaveChanges();
        return tracked;
    }

    public static WatchlistEntry SeedEntry(
        MediaServerDbContext database,
        AppUser user,
        TrackedTitle title,
        SeriesMonitorScope? scope = null,
        List<int>? monitoredSeasons = null,
        string? regionOverride = null,
        DateTimeOffset? createdAt = null)
    {
        var entry = new WatchlistEntry
        {
            Id = Guid.NewGuid(),
            AppUserId = user.Id,
            TrackedTitleId = title.Id,
            MonitorScope = scope,
            MonitoredSeasons = monitoredSeasons,
            RegionOverride = regionOverride,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
        database.WatchlistEntries.Add(entry);
        database.SaveChanges();
        return entry;
    }

    public static ReleaseReminder SeedReminder(
        MediaServerDbContext database,
        AppUser user,
        TrackedTitle title,
        ReleaseType type,
        int leadDays = 0,
        TimeOnly? notifyAt = null,
        bool active = true,
        DateTimeOffset? createdAt = null)
    {
        var reminder = new ReleaseReminder
        {
            Id = Guid.NewGuid(),
            AppUserId = user.Id,
            TrackedTitleId = title.Id,
            ReleaseType = type,
            LeadDays = leadDays,
            NotifyAt = notifyAt ?? new TimeOnly(9, 0),
            Active = active,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
        database.ReleaseReminders.Add(reminder);
        database.SaveChanges();
        return reminder;
    }

    public static TrackedRelease SeedRelease(
        MediaServerDbContext database,
        TrackedTitle title,
        ReleaseType type,
        DateOnly date,
        string? region = null,
        int? season = null,
        int? episode = null,
        int? rawType = null)
    {
        var release = new TrackedRelease
        {
            Id = Guid.NewGuid(),
            TrackedTitleId = title.Id,
            Region = region,
            Type = type,
            RawType = rawType,
            Season = season,
            Episode = episode,
            Date = date,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        database.TrackedReleases.Add(release);
        database.SaveChanges();
        return release;
    }
}

/// <summary>A fixed, settable clock with an explicit app timezone (defaults to UTC).</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now, TimeZoneInfo? timeZone = null) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public TimeZoneInfo TimeZone { get; set; } = timeZone ?? TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZone;
}

/// <summary>Programmable <see cref="IReleaseScheduleProvider"/> — the mocked TMDb client.</summary>
internal sealed class FakeScheduleProvider : IReleaseScheduleProvider
{
    public string Key => "tmdb";

    public Dictionary<string, MovieReleaseSchedule?> Movies { get; } = [];

    public Dictionary<string, SeriesReleaseSchedule?> Series { get; } = [];

    public Dictionary<(string ProviderId, int Season), IReadOnlyList<EpisodeAirDate>> Seasons { get; } = [];

    /// <summary>Every call, for asserting what was (not) fetched.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The region sets requested per movie call.</summary>
    public List<IReadOnlyCollection<string>> MovieRegionRequests { get; } = [];

    public Task<MovieReleaseSchedule?> GetMovieScheduleAsync(
        string providerId, IReadOnlyCollection<string> regions, CancellationToken cancellationToken)
    {
        Calls.Add($"movie:{providerId}");
        MovieRegionRequests.Add(regions);
        return Task.FromResult(Movies.GetValueOrDefault(providerId));
    }

    public Task<SeriesReleaseSchedule?> GetSeriesScheduleAsync(string providerId, CancellationToken cancellationToken)
    {
        Calls.Add($"series:{providerId}");
        return Task.FromResult(Series.GetValueOrDefault(providerId));
    }

    public Task<IReadOnlyList<EpisodeAirDate>> GetSeasonEpisodesAsync(
        string providerId, int season, CancellationToken cancellationToken)
    {
        Calls.Add($"season:{providerId}:{season}");
        return Task.FromResult(Seasons.GetValueOrDefault((providerId, season)) ?? []);
    }
}
