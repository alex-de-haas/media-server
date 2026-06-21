using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Configuration;

/// <summary>Covers the operator-editable settings store: singleton-row upsert and list normalization.</summary>
public sealed class AppSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly AppSettingsService _service;

    public AppSettingsServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        _service = new AppSettingsService(_database);
    }

    [Fact]
    public async Task GetCustomReleaseGroups_returns_empty_when_unset()
    {
        Assert.Empty(await _service.GetCustomReleaseGroupsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Update_creates_the_singleton_row_and_round_trips_through_the_json_column()
    {
        await _service.UpdateCustomReleaseGroupsAsync(["RARBG", "LostFilm.TV"], CancellationToken.None);

        var groups = await _service.GetCustomReleaseGroupsAsync(CancellationToken.None);
        Assert.Equal(["RARBG", "LostFilm.TV"], groups);
        // A second update targets the same row rather than inserting a duplicate.
        Assert.Equal(1, await _database.AppSettings.CountAsync());

        await _service.UpdateCustomReleaseGroupsAsync(["YTS"], CancellationToken.None);
        Assert.Equal(["YTS"], await _service.GetCustomReleaseGroupsAsync(CancellationToken.None));
        Assert.Equal(1, await _database.AppSettings.CountAsync());
    }

    [Fact]
    public async Task Update_trims_drops_blanks_and_dedupes_case_insensitively()
    {
        var saved = await _service.UpdateCustomReleaseGroupsAsync(["  RARBG ", "", "rarbg", "YTS"], CancellationToken.None);

        Assert.Equal(["RARBG", "YTS"], saved);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
