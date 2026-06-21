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
/// Operator re-search with a corrected title for a NeedsReview item. <see cref="Kind"/> defaults to the
/// catalog's kind (movie vs. series) when omitted. Metadata search only — the resulting candidates feed
/// the existing pick-to-<c>/match</c> flow; the library filename still derives from the chosen metadata.
/// </summary>
public sealed record MetadataSearchRequest(string Title, int? Year, MediaKind? Kind);
