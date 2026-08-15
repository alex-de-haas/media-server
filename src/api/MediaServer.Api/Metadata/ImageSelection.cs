using System.Linq.Expressions;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Metadata;

/// <summary>
/// Which of an item's cached images a surface shows. Artwork is stored language-tagged
/// (<see cref="ImageAsset.Language"/> is the provider's ISO 639-1 code, or null/empty for art carrying no
/// text at all), so every surface has to answer the same question: given several candidates, which one does
/// this reader want? The answer differs by <see cref="ImageType"/> rather than by instance — see
/// <c>docs/features/artwork-language/feature.md</c> — so it lives here once instead of at each call site.
/// </summary>
public static class ImageSelection
{
    /// <summary>An image the operator pinned outranks everything the ranking below could offer.</summary>
    private const int Pinned = -1;

    private const string English = "en";

    /// <summary>
    /// The provider's explicit "No Language" code, which its own UI labels <c>No Language (xx-XX)</c>. It means
    /// the same as an absent language — the image carries no text — and TMDb returns both: <c>null</c> when a
    /// language was never set, <c>xx</c> when it was deliberately set to none. Treating <c>xx</c> as a foreign
    /// language is what broke backdrop selection in other clients when TMDb started returning it, so both
    /// spellings collapse into the untagged tier here.
    /// </summary>
    private const string NoLanguage = "xx";

    /// <summary>
    /// Where each kind of candidate sits for a role, best (lowest) first. The display language always leads
    /// the tagged art and English is always the first fallback after it; what moves between roles is untagged
    /// art, because what "no text on the image" is worth depends entirely on what the image is for.
    /// </summary>
    private readonly record struct Tiers(int Display, int English, int Other, int Untagged)
    {
        /// <summary>A poster exists to name the title, so textless art is the last thing to show.</summary>
        public static readonly Tiers Poster = new(Display: 0, English: 1, Other: 2, Untagged: 3);

        /// <summary>
        /// A logo is a title treatment: an untagged one is a legitimate language-neutral wordmark
        /// (<c>TENET</c>), which is worth more than a title treatment in a language the reader cannot read.
        /// </summary>
        public static readonly Tiers Logo = new(Display: 0, English: 1, Other: 3, Untagged: 2);

        /// <summary>
        /// A backdrop is drawn under a locally rendered title, so text burned into it is a defect rather than
        /// a feature and textless art wins outright.
        /// </summary>
        public static readonly Tiers Backdrop = new(Display: 1, English: 2, Other: 3, Untagged: 0);
    }

    private static Tiers TiersFor(ImageType type) => type switch
    {
        ImageType.Backdrop => Tiers.Backdrop,
        ImageType.Logo => Tiers.Logo,
        _ => Tiers.Poster,
    };

    /// <summary>
    /// The tier a single candidate falls in. Public so a surface that has projected its own narrow row shape
    /// — rather than loaded whole <see cref="ImageAsset"/> entities — can still rank by the same rule instead
    /// of reimplementing it.
    /// </summary>
    public static int Tier(ImageType type, string? language, string displayLanguage) =>
        Tier(TiersFor(type), language, PrimarySubtag(displayLanguage));

    private static int Tier(Tiers tiers, string? language, string display)
    {
        if (string.IsNullOrEmpty(language) || string.Equals(language, NoLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return tiers.Untagged;
        }

        if (string.Equals(language, display, StringComparison.OrdinalIgnoreCase))
        {
            return tiers.Display;
        }

        return string.Equals(language, English, StringComparison.OrdinalIgnoreCase) ? tiers.English : tiers.Other;
    }

    /// <summary>
    /// The ordering key for <paramref name="type"/> under <paramref name="displayLanguage"/>, as an expression
    /// so a surface that selects in the database can rank there instead of materializing every candidate.
    /// Lower is better.
    /// </summary>
    /// <remarks>
    /// The language comparison is a plain equality against the primary subtag because provider image
    /// languages are bare ISO 639-1 codes — there is no region to strip on the stored side, and the provider
    /// emits lower case. Written as one conditional chain rather than composed from smaller expressions: EF
    /// Core translates a nested ternary to <c>CASE WHEN</c>, but cannot translate an invoked sub-expression.
    /// </remarks>
    public static Expression<Func<ImageAsset, int>> Rank(ImageType type, string displayLanguage)
    {
        var display = PrimarySubtag(displayLanguage);
        // Lifted into locals rather than read off the struct inside the tree: a captured `int` is a plain
        // query parameter, where member access on a captured struct leans on client-side evaluation.
        var (tierDisplay, tierEnglish, tierOther, tierUntagged) = TiersFor(type);

        return image => image.Language == null || image.Language == "" || image.Language == NoLanguage ? tierUntagged
            : image.Language == display ? tierDisplay
            : image.Language == English ? tierEnglish
            : tierOther;
    }

    /// <summary>
    /// The candidates of one type in preference order: tier, then the provider's own order, then the tag.
    /// <c>SortOrder</c> is only the position the image held in the response it arrived in — the provider
    /// documents no ordering for those arrays, so it is a weak preference rather than a quality signal. The tag
    /// is what makes the result stable: a re-enrich never renumbers rows already stored, so two images of one
    /// type can legitimately share a <c>SortOrder</c>, and without a total order the winner of such a tie would
    /// be whatever the database happened to return that request.
    /// </summary>
    public static IEnumerable<ImageAsset> InPreferenceOrder(
        this IEnumerable<ImageAsset> images, ImageType type, string displayLanguage, string? pinnedTag = null)
    {
        var tiers = TiersFor(type);
        var display = PrimarySubtag(displayLanguage);

        return images
            .Where(image => image.ImageType == type)
            .OrderBy(image => pinnedTag is { Length: > 0 } && string.Equals(image.Tag, pinnedTag, StringComparison.Ordinal)
                ? Pinned
                : Tier(tiers, image.Language, display))
            .ThenBy(image => image.SortOrder)
            .ThenBy(image => image.Tag, StringComparer.Ordinal);
    }

    /// <summary>The one image of <paramref name="type"/> to show, or null when the item has none.</summary>
    public static ImageAsset? Best(
        this IEnumerable<ImageAsset> images, ImageType type, string displayLanguage, string? pinnedTag = null) =>
        images.InPreferenceOrder(type, displayLanguage, pinnedTag).FirstOrDefault();

    /// <summary>
    /// The best poster per item as a remote URL — the shape every grid, rail and calendar needs. Ranking
    /// happens in the database so a large listing does not materialize every candidate, and the item's own pin
    /// is joined in rather than fetched separately. Chunked because the id list rides as SQL parameters, which
    /// SQLite caps at 999.
    /// </summary>
    public static async Task<Dictionary<Guid, string>> BestPosterUrlsAsync(
        this MediaServerDbContext database,
        IReadOnlyList<Guid> itemIds,
        string displayLanguage,
        CancellationToken cancellationToken)
    {
        var posters = new Dictionary<Guid, string>();
        if (itemIds.Count == 0)
        {
            return posters;
        }

        var (tierDisplay, tierEnglish, tierOther, tierUntagged) = Tiers.Poster;
        var display = PrimarySubtag(displayLanguage);

        foreach (var chunk in itemIds.Chunk(ChunkSize))
        {
            // The tier chain is inlined rather than taken from Rank because it has to be part of one
            // translatable expression tree together with the pin, which lives on the item rather than the
            // image. Tiers.Poster keeps the values themselves in one place.
            var rows = await database.ImageAssets.AsNoTracking()
                .Where(image => chunk.Contains(image.MediaItemId) && image.ImageType == ImageType.Primary)
                .Join(
                    database.MediaItems.AsNoTracking(),
                    image => image.MediaItemId,
                    item => item.Id,
                    (image, item) => new
                    {
                        image.MediaItemId,
                        image.RemotePath,
                        image.SortOrder,
                        image.Tag,
                        Tier = item.PreferredPosterTag != null && image.Tag == item.PreferredPosterTag ? Pinned
                            : image.Language == null || image.Language == "" || image.Language == NoLanguage ? tierUntagged
                            : image.Language == display ? tierDisplay
                            : image.Language == English ? tierEnglish
                            : tierOther,
                    })
                .ToListAsync(cancellationToken);

            foreach (var group in rows.GroupBy(row => row.MediaItemId))
            {
                posters[group.Key] = group
                    .OrderBy(row => row.Tier)
                    .ThenBy(row => row.SortOrder)
                    .ThenBy(row => row.Tag, StringComparer.Ordinal)
                    .First()
                    .RemotePath;
            }
        }

        return posters;
    }

    /// <summary>SQLite's parameter ceiling is 999; the id list rides as parameters in the IN-clause.</summary>
    private const int ChunkSize = 500;

    /// <summary>
    /// The language part of a BCP 47 tag, matching how <see cref="MetadataLanguage"/> compares languages: a
    /// whole subtag rather than a two-character prefix, which would read <c>fil-PH</c> as Finnish.
    /// </summary>
    private static string PrimarySubtag(string language)
    {
        var separator = language.IndexOf('-');
        return (separator < 0 ? language : language[..separator]).ToLowerInvariant();
    }
}
