using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;

namespace MediaServer.Api.Pipeline;

public sealed record IngestSourceFileResponse(
    Guid Id,
    string RelativePath,
    long SizeBytes,
    string AssignmentStatus,
    Guid? MediaItemId);

public sealed record IngestItemResponse(
    Guid Id,
    Guid CatalogId,
    Guid? DownloadId,
    Guid? MediaItemId,
    string Stage,
    string Status,
    int AttemptCount,
    IReadOnlyList<string> StagesCompleted,
    string? LastError,
    IReadOnlyList<MetadataCandidate> ReviewCandidates,
    IReadOnlyList<IngestSourceFileResponse> SourceFiles,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static IngestItemResponse From(IngestItem item, IReadOnlyList<SourceFile> sourceFiles)
    {
        var candidates = string.IsNullOrEmpty(item.ReviewCandidates)
            ? []
            : JsonSerializer.Deserialize<List<MetadataCandidate>>(item.ReviewCandidates) ?? [];

        return new IngestItemResponse(
            item.Id,
            item.CatalogId,
            item.DownloadId,
            item.MediaItemId,
            item.Stage.ToString(),
            item.Status.ToString(),
            item.AttemptCount,
            item.StagesCompleted,
            item.LastError,
            candidates,
            sourceFiles.Select(file => new IngestSourceFileResponse(
                file.Id, file.RelativePath, file.SizeBytes, file.AssignmentStatus.ToString(), file.MediaItemId)).ToList(),
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
