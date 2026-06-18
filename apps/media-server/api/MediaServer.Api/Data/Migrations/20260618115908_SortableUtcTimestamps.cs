using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <summary>
    /// Re-formats every <c>DateTimeOffset</c> column in place so its TEXT value sorts and compares
    /// chronologically in SQLite. Pairs with <see cref="MediaServer.Api.Data.UtcDateTimeOffsetConverter"/>,
    /// which now writes a fixed-width UTC ISO-8601 string (<c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>).
    ///
    /// The column type is unchanged (TEXT before and after), so EF scaffolds no schema operations — this
    /// migration only rewrites existing rows from the old EF default format
    /// (<c>yyyy-MM-dd HH:mm:ss[.fffffff]+00:00</c>, space separator, trailing offset) into the new
    /// canonical form. Rows already in the new format (11th char is 'T') are skipped, so the migration is
    /// safe to run against a fresh DB (no rows) or a partially-migrated one.
    /// </summary>
    /// <inheritdoc />
    public partial class SortableUtcTimestamps : Migration
    {
        // Every DateTimeOffset (and DateTimeOffset?) column in the model, by table.
        private static readonly (string Table, string Column)[] TimestampColumns =
        {
            ("AppUsers", "CreatedAt"),
            ("AppUsers", "LastSeenAt"),
            ("Catalogs", "CreatedAt"),
            ("Catalogs", "UpdatedAt"),
            ("Downloads", "AddedAt"),
            ("Downloads", "CompletedAt"),
            ("IngestItems", "CreatedAt"),
            ("IngestItems", "UpdatedAt"),
            ("IngestItems", "LeaseUntil"),
            ("IngestItems", "NextAttemptAt"),
            ("JellyfinAccessTokens", "CreatedAt"),
            ("JellyfinAccessTokens", "LastSeenAt"),
            ("JellyfinCredentials", "CreatedAt"),
            ("JellyfinCredentials", "LastUsedAt"),
            ("JellyfinCredentials", "LockedUntil"),
            ("Jobs", "StartedAt"),
            ("Jobs", "CompletedAt"),
            ("Jobs", "UpdatedAt"),
            ("MediaItems", "AddedAt"),
            ("MediaItems", "UpdatedAt"),
            ("MediaSources", "CreatedAt"),
            ("MetadataRecords", "ReleaseDate"),
            ("MetadataRecords", "FetchedAt"),
            ("SourceFiles", "CreatedAt"),
            ("SourceFiles", "UpdatedAt"),
            ("UserItemData", "LastPlayedDate"),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Old:  2026-06-18 11:34:42.605804+00:00   ->   New:  2026-06-18T11:34:42.6058040Z
            // Swap the date/time separator to 'T', normalize the fraction to exactly 7 digits (right-pad
            // with zeros, or '0000000' when absent), drop the +00:00 offset, and append the 'Z'.
            foreach (var (table, column) in TimestampColumns)
            {
                var c = $"\"{column}\"";
                migrationBuilder.Sql(
                    $"""
                    UPDATE "{table}" SET {c} =
                        substr({c}, 1, 10) || 'T' || substr({c}, 12, 8) || '.' ||
                        substr(
                            (CASE WHEN instr({c}, '.') = 0 THEN ''
                                  ELSE substr({c}, instr({c}, '.') + 1, instr({c}, '+') - instr({c}, '.') - 1) END)
                            || '0000000', 1, 7) || 'Z'
                    WHERE {c} IS NOT NULL AND substr({c}, 11, 1) = ' ';
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // New:  2026-06-18T11:34:42.6058040Z   ->   Old:  2026-06-18 11:34:42.6058040+00:00
            foreach (var (table, column) in TimestampColumns)
            {
                var c = $"\"{column}\"";
                migrationBuilder.Sql(
                    $"""
                    UPDATE "{table}" SET {c} =
                        substr({c}, 1, 10) || ' ' || substr({c}, 12, 8) || '.' || substr({c}, 21, 7) || '+00:00'
                    WHERE {c} IS NOT NULL AND substr({c}, 11, 1) = 'T';
                    """);
            }
        }
    }
}
