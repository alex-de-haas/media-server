using MediaServer.Api.Data;

namespace MediaServer.Api.Metadata;

/// <summary>One app-typed, dated movie release in a region (already bucketed from the provider's raw code).</summary>
public sealed record TypedReleaseDate(string Region, ReleaseType Type, int RawType, DateTimeOffset Date, string? Note);

/// <summary>
/// A movie's release schedule: typed dates for the requested regions plus a display/status snapshot for
/// the <c>TrackedTitle</c> row.
/// </summary>
public sealed record MovieReleaseSchedule(
    string? Title,
    int? Year,
    string? PosterUrl,
    string? Status,
    IReadOnlyList<TypedReleaseDate> Dates);

/// <summary>One series episode air date.</summary>
public sealed record EpisodeAirDate(int Season, int Episode, DateTimeOffset AirDate, string? Name);

/// <summary>
/// A series' title-level schedule from the single <c>/tv/{id}</c> call: status, the next/last episode to
/// air (free for every tracked series, no per-season fetch), and the known season numbers so opt-in
/// episode enumeration can resolve <c>WholeShow</c>/<c>FutureEpisodes</c> scopes.
/// </summary>
public sealed record SeriesReleaseSchedule(
    string? Title,
    int? Year,
    string? PosterUrl,
    string? Status,
    EpisodeAirDate? NextEpisode,
    EpisodeAirDate? LastEpisode,
    IReadOnlyList<int> Seasons);

/// <summary>
/// Typed release-date source for release tracking (<c>docs/features/release-tracking.md</c>). TMDb is the
/// first implementation. Kept separate from <see cref="IMetadataProvider"/> so the date-sync loop mocks a
/// small, purpose-built surface — and the certification path in the metadata provider stays untouched.
/// </summary>
public interface IReleaseScheduleProvider
{
    string Key { get; }

    /// <summary>
    /// Fetches a movie's typed release dates for the given regions (each entry already bucketed into an
    /// app <see cref="ReleaseType"/>; Physical/TV dropped). Returns null when the title is unknown or the
    /// call fails.
    /// </summary>
    Task<MovieReleaseSchedule?> GetMovieScheduleAsync(
        string providerId, IReadOnlyCollection<string> regions, CancellationToken cancellationToken);

    /// <summary>Fetches a series' title-level schedule (one call, no per-season enumeration).</summary>
    Task<SeriesReleaseSchedule?> GetSeriesScheduleAsync(string providerId, CancellationToken cancellationToken);

    /// <summary>Enumerates one season's episode air dates (opt-in, only for monitored seasons).</summary>
    Task<IReadOnlyList<EpisodeAirDate>> GetSeasonEpisodesAsync(
        string providerId, int season, CancellationToken cancellationToken);
}
