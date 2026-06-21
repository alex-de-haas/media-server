using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline;

/// <summary>The media items touched by one ingest item: assigned leaves (movies/episodes) plus their
/// series/season ancestors. Used by the organize/probe/enrich/publish stages.</summary>
public sealed record IngestGraph(
    IReadOnlyList<MediaItem> Leaves,
    IReadOnlyList<MediaItem> Containers)
{
    public IEnumerable<MediaItem> All => Leaves.Concat(Containers);

    public static async Task<IngestGraph> LoadAsync(MediaServerDbContext database, Guid ingestItemId, CancellationToken cancellationToken)
    {
        var leafIds = await database.SourceFiles
            .Where(file => file.IngestItemId == ingestItemId && file.MediaItemId != null)
            .Select(file => file.MediaItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var leaves = await database.MediaItems
            .Where(item => leafIds.Contains(item.Id))
            .ToListAsync(cancellationToken);

        var containerIds = leaves
            .SelectMany(leaf => new[] { leaf.SeriesId, leaf.SeasonId, leaf.ParentId })
            .OfType<Guid>()
            .Distinct()
            .Where(id => !leafIds.Contains(id))
            .ToList();

        var containers = containerIds.Count == 0
            ? new List<MediaItem>()
            : await database.MediaItems.Where(item => containerIds.Contains(item.Id)).ToListAsync(cancellationToken);

        return new IngestGraph(leaves, containers);
    }
}
