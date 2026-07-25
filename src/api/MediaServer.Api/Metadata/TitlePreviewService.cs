using System.Text.Json;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Metadata;

/// <summary>
/// What a title <em>is</em>, for a title the instance may not hold: the preview behind a recommendation
/// card, a tracked row, a calendar entry or a search result.
/// </summary>
/// <remarks>
/// A held title is answered from the library's own detail projection rather than from the provider — it
/// already has language-matched artwork and a cast with local person ids, and a preview must not state
/// anything different from the page it links to. Everything else is one TMDb detail request behind a
/// database cache.
/// </remarks>
public sealed class TitlePreviewService(
    MediaServerDbContext database,
    LibraryReadService library,
    IMetadataProvider provider,
    MediaServerSettings settings,
    TimeProvider time,
    ILogger<TitlePreviewService> logger)
{
    /// <summary>
    /// How long a cached payload stays usable. A title's overview, cast and runtime are settled facts;
    /// what does move — a series' status and episode counts — moves on the order of weeks.
    /// </summary>
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// The preview for one title, or null when the provider has no such title (and nothing is cached).
    /// </summary>
    public async Task<TitlePreviewDto?> GetAsync(ProviderRef reference, MediaKind kind, CancellationToken cancellationToken)
    {
        var mediaItemId = await FindLibraryItemIdAsync(reference, kind, cancellationToken);
        if (mediaItemId is { } id && await library.GetDetailAsync(id, null, cancellationToken) is { } detail)
        {
            return FromLibrary(reference, detail);
        }

        var payload = await LoadPayloadAsync(reference, kind, cancellationToken);
        return payload is null ? null : FromPayload(reference, kind, payload);
    }

    /// <summary>The published top-level item carrying this identity, if the instance holds one.</summary>
    /// <remarks>
    /// The same check the watchlist links titles with: published items only, movies matching movies and
    /// series matching series, so a preview never claims playback for something unpublished.
    /// </remarks>
    private async Task<Guid?> FindLibraryItemIdAsync(ProviderRef reference, MediaKind kind, CancellationToken cancellationToken) =>
        await database.MediaItems.AsNoTracking()
            .Where(item => item.Kind == kind && item.PublicId != null && item.ParentId == null
                && item.IdentityProvider == reference.Provider && item.IdentityProviderId == reference.Id)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private static TitlePreviewDto FromLibrary(ProviderRef reference, LibraryDetailDto detail) => new(
        reference.Provider,
        reference.Id,
        detail.Kind,
        detail.Title,
        detail.OriginalTitle,
        detail.Year,
        detail.Overview,
        detail.Tagline,
        detail.Genres,
        detail.PosterUrl,
        detail.BackdropUrl,
        detail.OfficialRating,
        detail.CommunityRating,
        detail.VoteCount,
        detail.RuntimeTicks,
        detail.Status,
        detail.SeasonCount,
        detail.EpisodeCount,
        detail.Directors,
        detail.Creators,
        detail.Cast,
        detail.TrailerUrl,
        detail.ImdbId,
        detail.Homepage,
        InLibrary: true,
        MediaItemId: detail.Id);

    private TitlePreviewDto FromPayload(ProviderRef reference, MediaKind kind, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var meta = TmdbPayload.MapDetails(reference, Language, kind, document.RootElement);
        var facts = TmdbPayload.Parse(payload, kind);

        return new TitlePreviewDto(
            reference.Provider,
            reference.Id,
            kind.ToString(),
            // The provider answers with a localized title; the original is the only fallback if it does not.
            meta.Title ?? meta.OriginalTitle ?? string.Empty,
            meta.OriginalTitle,
            meta.ReleaseDate?.Year,
            meta.Overview,
            meta.Tagline,
            meta.Genres,
            facts.PosterUrl,
            facts.BackdropUrl,
            meta.OfficialRating,
            meta.CommunityRating,
            facts.VoteCount,
            meta.RuntimeTicks,
            facts.Status,
            facts.SeasonCount,
            facts.EpisodeCount,
            facts.Directors,
            facts.Creators,
            // Parsed from the payload's credits: a title nobody holds has no local Person rows to join.
            facts.Cast.Select(member => new CastMemberDto(
                reference.Provider, member.ProviderId, member.Name, member.Character, member.ProfileUrl)).ToList(),
            facts.TrailerUrl,
            facts.ImdbId,
            facts.Homepage,
            InLibrary: false,
            MediaItemId: null);
    }

    /// <summary>The raw detail payload, from cache while fresh and from the provider otherwise.</summary>
    private async Task<string?> LoadPayloadAsync(ProviderRef reference, MediaKind kind, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var language = Language;
        var cached = await database.TmdbTitleDetailCache.FirstOrDefaultAsync(
            row => row.Kind == kind && row.TmdbId == reference.Id && row.Language == language, cancellationToken);

        // The TTL is enforced on read, so a stale row is a miss — and the write target below.
        if (cached is not null && now - cached.FetchedAt < CacheLifetime)
        {
            return cached.Payload;
        }

        var fetched = await FetchAsync(reference, kind, language, cancellationToken);
        if (fetched is null)
        {
            // The provider did not answer. A week-old overview is a better preview than an error, and the
            // facts in it were true when they were written.
            return cached?.Payload;
        }

        await StoreAsync(cached, reference, kind, language, fetched, now, cancellationToken);
        return fetched;
    }

    private async Task<string?> FetchAsync(
        ProviderRef reference, MediaKind kind, string language, CancellationToken cancellationToken)
    {
        try
        {
            var records = await provider.FetchAsync(reference, kind, [language], cancellationToken);
            return records.Count > 0 ? records[0].Raw : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Details for {Provider}:{Id} could not be fetched.", reference.Provider, reference.Id);
            return null;
        }
    }

    private async Task StoreAsync(
        TmdbTitleDetailCacheEntry? existing,
        ProviderRef reference,
        MediaKind kind,
        string language,
        string payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (existing is not null)
        {
            existing.Payload = payload;
            existing.FetchedAt = now;
        }
        else
        {
            database.TmdbTitleDetailCache.Add(new TmdbTitleDetailCacheEntry
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                TmdbId = reference.Id,
                Language = language,
                Payload = payload,
                FetchedAt = now,
            });
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Two users can open the same title at once and race on the unique index. The fetch already
            // succeeded, so this caller has its answer; the other writer's row is equally good.
            logger.LogDebug(exception, "A concurrent write already cached details for {Provider}:{Id}.", reference.Provider, reference.Id);
            database.ChangeTracker.Clear();
        }
    }

    /// <summary>The primary configured metadata language — the one the rest of the UI reads titles in.</summary>
    private string Language => settings.SupportedLanguages.Count > 0 ? settings.SupportedLanguages[0] : "en-US";
}
