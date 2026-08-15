using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// One artwork candidate an operator can choose between. <see cref="Language"/> is the provider's tag for the
/// language of the text printed on the image — null when it carries none — and <see cref="Selected"/> marks
/// the one the surfaces are showing today, whether that is the pin or the ranking's answer.
/// </summary>
public sealed record ItemImageDto(
    string Type,
    string Tag,
    string Url,
    string? Language,
    int SortOrder,
    bool Pinned,
    bool Selected);

/// <summary>
/// The artwork an item holds, and the operator's override of which poster to use.
///
/// The candidates are already cached by enrich, so listing them costs no provider request; pinning one is
/// how an operator answers the case no ranking can (see <c>docs/features/artwork-language/feature.md</c>) —
/// a sequel whose only localized poster carries no title, where the automatic choice cannot know which film
/// the picture is of.
/// </summary>
public sealed class ItemArtworkService(MediaServerDbContext database, MediaServerSettings settings)
{
    /// <summary>
    /// Every cached image for an item, grouped by role and ordered exactly as the surfaces rank them, so the
    /// first entry of each role is the one on screen. Null when the item does not exist.
    /// </summary>
    public async Task<IReadOnlyList<ItemImageDto>?> ListAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.AsNoTracking()
            .Where(candidate => candidate.Id == itemId)
            .Select(candidate => new { candidate.PreferredPosterTag })
            .FirstOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return null;
        }

        var images = await database.ImageAssets.AsNoTracking()
            .Where(image => image.MediaItemId == itemId)
            .ToListAsync(cancellationToken);

        var candidates = new List<ItemImageDto>(images.Count);
        foreach (var type in (ImageType[])[ImageType.Primary, ImageType.Backdrop, ImageType.Logo])
        {
            var pinnedTag = type == ImageType.Primary ? item.PreferredPosterTag : null;
            var ranked = images.InPreferenceOrder(type, settings.PreferredLanguage, pinnedTag).ToList();
            for (var index = 0; index < ranked.Count; index++)
            {
                var image = ranked[index];
                var pinned = pinnedTag is { Length: > 0 } && string.Equals(image.Tag, pinnedTag, StringComparison.Ordinal);
                candidates.Add(new ItemImageDto(
                    type.ToString(),
                    image.Tag,
                    image.RemotePath,
                    image.Language,
                    image.SortOrder,
                    pinned,
                    Selected: index == 0));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Pins the poster carrying <paramref name="tag"/>. Refuses a tag the item does not hold — a pin that
    /// matches nothing would silently do nothing and read as success — and a tag belonging to one of its
    /// backdrops or logos, which are not posters and cannot stand in for one.
    /// </summary>
    public async Task<PinPosterResult> PinAsync(Guid itemId, string? tag, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
        {
            return PinPosterResult.NotFound;
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            return PinPosterResult.InvalidTag;
        }

        var isPoster = await database.ImageAssets.AsNoTracking().AnyAsync(
            image => image.MediaItemId == itemId && image.ImageType == ImageType.Primary && image.Tag == tag,
            cancellationToken);
        if (!isPoster)
        {
            return PinPosterResult.InvalidTag;
        }

        item.PreferredPosterTag = tag;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return PinPosterResult.Ok;
    }

    /// <summary>
    /// Clears the pin, handing the choice back to the ranking. Clearing an item that has no pin is not an
    /// error — the caller asked for "no pin" and that is the state it is in.
    /// </summary>
    public async Task<bool> ClearAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
        {
            return false;
        }

        if (item.PreferredPosterTag is not null)
        {
            item.PreferredPosterTag = null;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}

public enum PinPosterResult
{
    Ok,
    NotFound,
    InvalidTag,
}

/// <summary>The poster to pin, identified by its <see cref="ImageAsset.Tag"/>.</summary>
public sealed record PinPosterRequest(string? Tag);
