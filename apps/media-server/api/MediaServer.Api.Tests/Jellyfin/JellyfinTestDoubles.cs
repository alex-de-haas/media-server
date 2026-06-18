using MediaServer.Api.Data;
using MediaServer.Api.Jellyfin.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Jellyfin;

/// <summary>A controllable clock for exercising lockout windows deterministically.</summary>
internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>
/// A trivial PIN hasher so credential/lockout tests stay fast and deterministic. The real argon2id
/// hasher is covered separately by <see cref="PinHasherTests"/>.
/// </summary>
internal sealed class FakePinHasher : IPinHasher
{
    public string Hash(string pin) => "fake:" + pin;

    public bool Verify(string pin, string encodedHash) => encodedHash == "fake:" + pin;
}

/// <summary>Owns an in-memory SQLite database with the schema migrated, for Jellyfin-surface tests.</summary>
internal sealed class JellyfinDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public JellyfinDatabase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        Context = Create();
        Context.Database.Migrate();
    }

    public MediaServerDbContext Context { get; }

    public MediaServerDbContext Create() =>
        new(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
