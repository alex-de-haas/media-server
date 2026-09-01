using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MediaServer.Api.Data;

/// <summary>
/// Replaces SQLite's <c>like()</c> with one that folds case for every alphabet, not just ASCII.
/// </summary>
/// <remarks>
/// <para>
/// SQLite's built-in LIKE is case-insensitive for A-Z and nothing else, and its <c>lower()</c> has the
/// same limit — so a search for <c>оппенгеймер</c> misses <c>Оппенгеймер</c>, and lowering the term in
/// .NET before comparing makes it worse rather than better, because the column side never folds. Both
/// were measured before this was written.
/// </para>
/// <para>
/// LIKE is an ordinary function in SQLite and an application may replace it, which is what this does:
/// every <c>LIKE</c> in the app then folds through .NET. The alternative — a normalized column beside
/// every searchable one — costs a migration, a backfill, and a write path to keep in step, for the
/// same result. It also fixes the two Jellyfin searches, which had the bug and no test for it.
/// </para>
/// <para>
/// The cost is that SQLite can no longer use its LIKE-to-range index optimization. That optimization
/// only ever applied to prefix patterns; every pattern here is <c>%term%</c>, which scans regardless.
/// </para>
/// </remarks>
public static class SqliteUnicodeLike
{
    // Patterns repeat across rows and across requests, and translating one is more expensive than
    // matching with it. Bounded because the pattern is caller-supplied: an agent searching a thousand
    // distinct titles must not be able to grow this without limit.
    private const int MaxCachedPatterns = 512;

    private static readonly ConcurrentDictionary<(string Pattern, char Escape), Regex> Cache = new();

    /// <summary>Registers the replacement on one connection. Safe to call more than once.</summary>
    public static void Register(SqliteConnection connection)
    {
        // Two arities, because `x LIKE y` and `x LIKE y ESCAPE z` are different functions to SQLite.
        // Note the argument order: the pattern comes first, the value second — the reverse of the SQL.
        connection.CreateFunction<string?, string?, bool?>(
            "like", (pattern, value) => Matches(pattern, value, escape: null));
        connection.CreateFunction<string?, string?, string?, bool?>(
            "like", (pattern, value, escape) => Matches(pattern, value, escape));
    }

    private static bool? Matches(string? pattern, string? value, string? escape)
    {
        // SQL three-valued logic: a comparison involving NULL is NULL, not false. Returning false here
        // would quietly turn `WHERE title LIKE ...` into a filter that also drops the NULL rows for a
        // different reason than the caller asked for.
        if (pattern is null || value is null)
        {
            return null;
        }

        var escapeChar = string.IsNullOrEmpty(escape) ? '\0' : escape[0];
        var regex = Cache.Count >= MaxCachedPatterns
            ? Translate(pattern, escapeChar)
            : Cache.GetOrAdd((pattern, escapeChar), key => Translate(key.Pattern, key.Escape));
        return regex.IsMatch(value);
    }

    private static Regex Translate(string pattern, char escape)
    {
        var builder = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (escape != '\0' && current == escape && index + 1 < pattern.Length)
            {
                // The escaped character is data: `\%` means a literal percent, not "anything".
                builder.Append(Regex.Escape(pattern[++index].ToString()));
                continue;
            }

            builder.Append(current switch
            {
                '%' => ".*",
                '_' => ".",
                _ => Regex.Escape(current.ToString()),
            });
        }

        return new Regex(
            builder.Append('$').ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }
}

/// <summary>Registers <see cref="SqliteUnicodeLike"/> on every connection EF opens.</summary>
/// <remarks>
/// A connection handed to EF already open is never opened *by* EF, so this never fires for one — the
/// test harness shares a single open connection and calls <see cref="SqliteUnicodeLike.Register"/>
/// itself. That is why registration is a public method rather than living inside this class: a hook
/// that silently does not run would leave the tests asserting the built-in LIKE.
/// </remarks>
public sealed class SqliteUnicodeLikeInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return Task.CompletedTask;
    }

    private static void Apply(DbConnection connection)
    {
        if (connection is SqliteConnection sqlite)
        {
            SqliteUnicodeLike.Register(sqlite);
        }
    }
}
