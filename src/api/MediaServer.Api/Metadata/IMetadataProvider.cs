using MediaServer.Api.Data;

namespace MediaServer.Api.Metadata;

public sealed record MediaQuery(MediaKind Kind, string Title, int? Year, int? Season = null, int? Episode = null);

public sealed record ProviderRef(string Provider, string Id);

public sealed record MetadataCandidate(ProviderRef Reference, string Title, int? Year, double Score);

/// <summary>One provider record for a single language; <see cref="Raw"/> keeps the full payload.</summary>
public sealed record ProviderMetadata(
    ProviderRef Reference,
    string Language,
    string? Title,
    string? OriginalTitle,
    string? OriginalLanguage,
    string? Overview,
    string? Tagline,
    IReadOnlyList<string> Genres,
    string? OfficialRating,
    double? CommunityRating,
    DateTimeOffset? ReleaseDate,
    long? RuntimeTicks,
    string? Raw);

public sealed record RemoteImage(ImageType Type, string? Language, string RemotePath, int SortOrder);

/// <summary>
/// Search/match/fetch/images abstraction; TMDb is the first implementation. Identify uses
/// <see cref="SearchAsync"/> + scoring; enrich uses <see cref="FetchAsync"/>/<see cref="GetImagesAsync"/>
/// to populate <c>MetadataRecord</c>/<c>ImageAsset</c> keyed by provider+language. See
/// <c>docs/features/metadata.md</c>.
/// </summary>
public interface IMetadataProvider
{
    string Key { get; }

    Task<IReadOnlyList<MetadataCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderMetadata>> FetchAsync(ProviderRef reference, MediaKind kind, IReadOnlyList<string> languages, CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteImage>> GetImagesAsync(ProviderRef reference, MediaKind kind, IReadOnlyList<string> languages, CancellationToken cancellationToken);
}
