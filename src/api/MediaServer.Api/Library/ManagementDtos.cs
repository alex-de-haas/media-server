namespace MediaServer.Api.Library;

public sealed record ManagementQuery(
    string Kind = "Movie", Guid? CatalogId = null, Guid? ItemId = null, Guid? SeriesId = null,
    int? Season = null, string? Search = null, string View = "all", string? SourceKind = null,
    string? Resolution = null, string? Codec = null, string? Hdr = null, string? DvProfile = null,
    string? State = null, int? MinVersions = null, int? MaxVersions = null,
    string Sort = "title", bool Descending = false, int Limit = 25, int Offset = 0,
    Guid? FocusId = null);

public sealed record ManagementItemDto(
    Guid Id, string Kind, string Title, int? Year, Guid? CatalogId, string CatalogName,
    int VersionCount, long? SizeBytes, string Availability, int IssueCount,
    int AffectedEpisodes, int EpisodeCount, int SeasonCount, int ActiveOperations,
    int? IndexNumber, int? IndexNumberEnd, int? ParentIndexNumber);

public sealed record ManagementPage(
    IReadOnlyList<ManagementItemDto> Items, int Total, int Limit, int Offset,
    IReadOnlyDictionary<string, int> Counts, string Reference);

public sealed record ManagementSourceDto(
    Guid Id, string Availability, string? Reason, bool DolbyVisionFallback,
    bool DolbyVisionUnknown, bool MatchesFilter);

public sealed record LibraryAvailabilityDto(Guid Id, string Availability, bool DolbyVisionFallback);
