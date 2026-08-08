using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Remux;

/// <summary>Whether a source can be served repackaged, and if not, whether waiting would help.</summary>
public enum RemuxReadinessState
{
    /// <summary>Nothing here can index this container, and nothing later will. Waiting is pointless.</summary>
    Unsupported,

    /// <summary>Indexable, but the background walk has not reached it. Retrying later succeeds.</summary>
    Pending,

    /// <summary>Indexed against the file as it stands. A URL handed out now will open.</summary>
    Ready,
}

/// <summary>
/// Whether a source can be served repackaged right now.
///
/// The distinction is the point. Packaging itself always works, so a flat "unavailable" would be wrong;
/// but a container nothing can index is not merely "not yet", and telling a client to wait for something
/// that will never arrive is worse than telling it no.
/// </summary>
public interface IRemuxReadiness
{
    Task<IReadOnlyDictionary<Guid, RemuxReadinessState>> ReadyAsync(
        IReadOnlyList<Guid> mediaSourceIds, CancellationToken cancellationToken);
}

public sealed class RemuxReadiness(
    MediaServerDbContext database, ICatalogPathSandbox sandbox, RemuxIndexStore store) : IRemuxReadiness
{
    public async Task<IReadOnlyDictionary<Guid, RemuxReadinessState>> ReadyAsync(
        IReadOnlyList<Guid> mediaSourceIds, CancellationToken cancellationToken)
    {
        var states = new Dictionary<Guid, RemuxReadinessState>();
        if (mediaSourceIds.Count == 0)
        {
            return states;
        }

        var rows = await database.MediaSources.AsNoTracking()
            .Where(source => mediaSourceIds.Contains(source.Id))
            .Join(database.MediaItems.AsNoTracking(), source => source.MediaItemId, item => item.Id,
                (source, item) => new { source.Id, source.Path, source.Container, item.CatalogId })
            .ToListAsync(cancellationToken);

        var catalogIds = rows.Where(row => row.CatalogId != null).Select(row => row.CatalogId!.Value).Distinct();
        var catalogs = await database.Catalogs.AsNoTracking()
            .Where(catalog => catalogIds.Contains(catalog.Id))
            .ToDictionaryAsync(catalog => catalog.Id, cancellationToken);

        foreach (var row in rows)
        {
            if (!RemuxIndexService.IsIndexable(row.Container))
            {
                states[row.Id] = RemuxReadinessState.Unsupported;
                continue;
            }

            states[row.Id] = row.CatalogId is { } catalogId
                && catalogs.TryGetValue(catalogId, out var catalog)
                && sandbox.TryResolve(catalog, row.Path, out var absolute)
                && store.IsCurrent(row.Id, absolute)
                    ? RemuxReadinessState.Ready
                    : RemuxReadinessState.Pending;
        }

        return states;
    }
}
