using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Metadata;

/// <summary>
/// Keeps <see cref="MetadataTag"/> in step with the metadata records it projects.
/// </summary>
/// <remarks>
/// Genres are stored as a converted JSON list and keywords only exist inside the raw provider payload,
/// so neither can be filtered on in SQL. This rebuilds both as rows a query can reach, which is what
/// lets "an action comedy" and "something about a plane hijacking" be answered at all.
///
/// Rebuilt rather than merged: a re-fetch that drops a genre has to drop the tag with it, and
/// reconciling two sets costs more than replacing one small one.
/// </remarks>
public sealed class MetadataTagSync(MediaServerDbContext database, ILogger<MetadataTagSync> logger)
{
    /// <summary>Rebuilds the tags of every metadata record belonging to one item.</summary>
    public async Task SyncAsync(Guid mediaItemId, MediaKind kind, CancellationToken cancellationToken)
    {
        var records = await database.MetadataRecords
            .Where(record => record.MediaItemId == mediaItemId)
            .ToListAsync(cancellationToken);
        if (records.Count == 0)
        {
            return;
        }

        var recordIds = records.Select(record => record.Id).ToList();
        var existing = await database.MetadataTags
            .Where(tag => recordIds.Contains(tag.MetadataRecordId))
            .ToListAsync(cancellationToken);
        database.MetadataTags.RemoveRange(existing);

        foreach (var record in records)
        {
            foreach (var tag in TagsFor(record, kind))
            {
                database.MetadataTags.Add(tag);
            }
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The tags one record projects to, deduplicated within the record.</summary>
    public IEnumerable<MetadataTag> TagsFor(MetadataRecord record, MediaKind kind)
    {
        var seen = new HashSet<(MetadataTagKind, string)>();

        foreach (var genre in record.Genres)
        {
            foreach (var tag in Emit(record, MetadataTagKind.Genre, genre, seen))
            {
                yield return tag;
            }
        }

        foreach (var keyword in Keywords(record, kind))
        {
            foreach (var tag in Emit(record, MetadataTagKind.Keyword, keyword, seen))
            {
                yield return tag;
            }
        }
    }

    private static IEnumerable<MetadataTag> Emit(
        MetadataRecord record, MetadataTagKind kind, string? value, HashSet<(MetadataTagKind, string)> seen)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !seen.Add((kind, trimmed)))
        {
            yield break;
        }

        yield return new MetadataTag
        {
            Id = Guid.NewGuid(),
            MetadataRecordId = record.Id,
            Kind = kind,
            Value = trimmed,
        };
    }

    private IReadOnlyList<string> Keywords(MetadataRecord record, MediaKind kind)
    {
        if (string.IsNullOrWhiteSpace(record.Raw))
        {
            return [];
        }

        try
        {
            return TmdbPayload.Parse(record.Raw, kind).Keywords;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A payload this cannot read costs one record's keywords, never the sync. The title stays
            // searchable by genre and by prose; it is simply missing from keyword matches, which is why
            // this is logged rather than swallowed.
            logger.LogWarning(exception, "Could not read keywords from metadata record {RecordId}.", record.Id);
            return [];
        }
    }
}
