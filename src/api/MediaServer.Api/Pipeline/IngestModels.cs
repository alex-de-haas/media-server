using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;

namespace MediaServer.Api.Pipeline;

public sealed record IngestSourceFileResponse(
    Guid Id,
    string RelativePath,
    long SizeBytes,
    string AssignmentStatus,
    Guid? MediaItemId,
    // Name-parsed hints surfaced to the review UI so it can pre-fill the corrected title and the
    // per-file season/episode without the operator retyping them. Computed on read from RelativePath
    // (no persisted column); null season/episode for movie catalogs or when the name has no SxxEyy.
    string ParsedTitle,
    int? ParsedYear,
    int? ParsedSeason,
    int? ParsedEpisode);

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
        INameParser parser, CatalogType catalogType, IReadOnlyCollection<string> releaseGroups)
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
                return new IngestSourceFileResponse(
                    file.Id, file.RelativePath, file.SizeBytes, file.AssignmentStatus.ToString(), file.MediaItemId,
                    parsed.Title, parsed.Year, parsed.Season, parsed.Episode);
            }).ToList(),
            item.CreatedAt,
            item.UpdatedAt);
    }
}

/// <summary>Operator manual-match for a NeedsReview source file (resumes the pipeline).</summary>
public sealed record MatchRequest(
    Guid SourceFileId,
    MediaKind Kind,
    string Provider,
    string ProviderId,
    string Title,
    int? Year,
    int? Season,
    int? Episode);

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
    AlreadyMatched,
    Skipped,
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
