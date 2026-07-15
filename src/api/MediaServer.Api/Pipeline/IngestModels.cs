using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// Where an already-mapped source file points: the media item's kind and title plus, for episodes, the
/// season/episode numbers and owning series. <see cref="Provider"/>/<see cref="ProviderId"/> carry the
/// identity used for the mapping (an episode's identity is its series' provider reference), so the review
/// UI can pre-select the same series when offering to re-map the batch. Null for extras, which carry no
/// provider identity of their own.
/// </summary>
public sealed record IngestAssignedMedia(
    string Kind,
    string Title,
    int? Season,
    int? Episode,
    string? SeriesTitle,
    string? Provider,
    string? ProviderId);

public sealed record IngestSourceFileResponse(
    Guid Id,
    string RelativePath,
    long SizeBytes,
    string AssignmentStatus,
    Guid? MediaItemId,
    // The current mapping for a Confirmed file (what the review UI shows and lets the operator change
    // while the batch is still in review), null while unmapped.
    IngestAssignedMedia? Assigned,
    // Name-parsed hints surfaced to the review UI so it can pre-fill the corrected title and the
    // per-file season/episode without the operator retyping them. Computed on read from RelativePath
    // (no persisted column); null season/episode for movie catalogs or when the name has no SxxEyy.
    string ParsedTitle,
    int? ParsedYear,
    int? ParsedSeason,
    int? ParsedEpisode,
    // Extras classification (see ExtraClassifier), likewise computed on read. When set, the review UI
    // pre-suggests attaching the file to its series as an extra titled ExtraTitle — or skipping it when
    // ExtraSuggestSkip is true (disc menus, commercials). All null/false for regular content.
    string? ExtraKind,
    string? ExtraTitle,
    bool ExtraSuggestSkip);

public sealed record IngestItemResponse(
    Guid Id,
    Guid CatalogId,
    Guid? DownloadId,
    string? DownloadName,
    string? MediaTitle,
    Guid? MediaItemId,
    // Operator- or acquisition-pinned target identity (all null when nothing is pinned). Lets the UI show a
    // "will be identified as …" badge and pre-fill the pin dialog. TargetKind is "Movie" or "Series".
    string? TargetProvider,
    string? TargetProviderId,
    string? TargetKind,
    string? TargetTitle,
    int? TargetYear,
    string Stage,
    string Status,
    int AttemptCount,
    IReadOnlyList<string> StagesCompleted,
    string? LastError,
    DateTimeOffset? NextAttemptAt,
    IReadOnlyList<MetadataCandidate> ReviewCandidates,
    IReadOnlyList<IngestSourceFileResponse> SourceFiles,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static IngestItemResponse From(
        IngestItem item, IReadOnlyList<SourceFile> sourceFiles, string? downloadName, string? mediaTitle,
        INameParser parser, CatalogType catalogType, IReadOnlyCollection<string> releaseGroups,
        IReadOnlyDictionary<Guid, IngestAssignedMedia> assignedMedia)
    {
        var candidates = string.IsNullOrEmpty(item.ReviewCandidates)
            ? []
            : JsonSerializer.Deserialize<List<MetadataCandidate>>(item.ReviewCandidates) ?? [];

        return new IngestItemResponse(
            item.Id,
            item.CatalogId,
            item.DownloadId,
            downloadName,
            mediaTitle,
            item.MediaItemId,
            item.TargetProvider,
            item.TargetProviderId,
            item.TargetKind?.ToString(),
            item.TargetTitle,
            item.TargetYear,
            item.Stage.ToString(),
            item.Status.ToString(),
            item.AttemptCount,
            item.StagesCompleted,
            item.LastError,
            item.NextAttemptAt,
            candidates,
            sourceFiles.Select(file =>
            {
                var name = Path.GetFileName(file.RelativePath);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = downloadName ?? file.RelativePath;
                }

                var parsed = parser.Parse(name, catalogType, releaseGroups);
                var extra = ExtraClassifier.Classify(file.RelativePath, catalogType);
                var assigned = file.MediaItemId is { } mediaItemId ? assignedMedia.GetValueOrDefault(mediaItemId) : null;
                return new IngestSourceFileResponse(
                    file.Id, file.RelativePath, file.SizeBytes, file.AssignmentStatus.ToString(), file.MediaItemId, assigned,
                    parsed.Title, parsed.Year, parsed.Season, parsed.Episode,
                    extra?.Kind.ToString(), extra?.Title, extra?.SuggestSkip ?? false);
            }).ToList(),
            item.CreatedAt,
            item.UpdatedAt);
    }
}

/// <summary>
/// Operator manual-match for source files of an item in review (resumes the pipeline). One provider
/// identity — the series for episodes, the movie itself otherwise — applies to every file in
/// <see cref="Files"/>; a batch never mixes titles, so the operator confirms the identity once and only
/// the per-file season/episode vary. Files already auto-matched may be re-matched this way while the item
/// is still in review (nothing has been organized yet).
/// </summary>
public sealed record MatchRequest(
    MediaKind Kind,
    string Provider,
    string ProviderId,
    string Title,
    int? Year,
    IReadOnlyList<MatchFileRequest> Files);

/// <summary>One file of a <see cref="MatchRequest"/>. Season/episode apply to episode matches only.</summary>
public sealed record MatchFileRequest(Guid SourceFileId, int? Season, int? Episode);

/// <summary>Result of a <c>MatchAsync</c> request, mapped to a status code by the endpoint.</summary>
public enum MatchOutcome
{
    NotFound,
    FileNotFound,
    AlreadyOrganized,
    Matched,
}

/// <summary>
/// Operator skip for NeedsReview source files that have no matchable identity (creditless OP/EDs, menus,
/// and other extras absent from the metadata provider). Skipped files count as resolved so the rest of the
/// batch proceeds; they are never organized or probed and are cleaned up with the staging leftovers.
/// </summary>
public sealed record SkipRequest(IReadOnlyList<Guid> SourceFileIds);

/// <summary>Result of a <c>SkipAsync</c> request, mapped to a status code by the endpoint.</summary>
public enum SkipOutcome
{
    NotFound,
    FileNotFound,
    AlreadyOrganized,
    Skipped,
}

/// <summary>
/// Operator attach of NeedsReview source files to a series as playable extras — the richer alternative to
/// skipping for content worth keeping (creditless OP/EDs, PVs, specials the provider doesn't list). The
/// series is the usual provider identity (get-or-create, like a match); each file becomes its own
/// <see cref="MediaKind.Video"/> item titled from its extras classification (file name as fallback) and then
/// flows through Organize (the series' <c>extras/</c> folder), Probe, and Publish like any other leaf.
/// <see cref="Season"/> optionally parents the extras under that season instead of the series root.
/// </summary>
public sealed record AssignExtrasRequest(
    IReadOnlyList<Guid> SourceFileIds,
    string Provider,
    string ProviderId,
    string Title,
    int? Year,
    int? Season);

/// <summary>Result of an <c>AssignExtrasAsync</c> request, mapped to a status code by the endpoint.</summary>
public enum AssignExtrasOutcome
{
    NotFound,
    FileNotFound,
    MovieCatalog,
    AlreadyOrganized,
    Assigned,
}

/// <summary>
/// Operator re-search with a corrected title for a NeedsReview item. <see cref="Kind"/> defaults to the
/// catalog's kind (movie vs. series) when omitted. Metadata search only — the resulting candidates feed
/// the existing pick-to-<c>/match</c> flow; the library filename still derives from the chosen metadata.
/// </summary>
public sealed record MetadataSearchRequest(string Title, int? Year, MediaKind? Kind);

/// <summary>
/// Pins a target identity on an ingest item before/while it downloads, so Identify resolves straight to it
/// instead of parsing + searching (and never routes to review). <see cref="Kind"/> is <see cref="MediaKind.Movie"/>
/// for a movie or <see cref="MediaKind.Series"/> for the owning series (per-file season/episode still come from
/// the file name). Rejected once the item has already been identified — correct a published item via library remap.
/// </summary>
public sealed record PinIdentityRequest(string Provider, string ProviderId, MediaKind Kind, string Title, int? Year);

/// <summary>Result of a <c>PinAsync</c> request, mapped to a status code by the endpoint.</summary>
public enum PinOutcome
{
    NotFound,
    AlreadyIdentified,
    InvalidKind,
    Pinned,
}
