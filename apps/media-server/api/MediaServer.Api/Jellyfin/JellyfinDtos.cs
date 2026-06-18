namespace MediaServer.Api.Jellyfin;

/// <summary>
/// The subset of Jellyfin DTOs the compatibility surface emits. Field names are PascalCase and
/// serialized verbatim (see <see cref="JellyfinJson"/>); only the fields Infuse needs are modeled.
/// </summary>
public sealed record QueryResult<T>(IReadOnlyList<T> Items, int TotalRecordCount, int StartIndex = 0);

public sealed record SystemInfoPublic(
    string Id,
    string ServerName,
    string Version,
    string ProductName = "Jellyfin Server",
    string OperatingSystem = "",
    bool StartupWizardCompleted = true);

public sealed record SystemInfo(
    string Id,
    string ServerName,
    string Version,
    string OperatingSystem = "",
    string ProductName = "Jellyfin Server",
    bool StartupWizardCompleted = true,
    bool SupportsLibraryMonitor = false,
    bool HasUpdateAvailable = false,
    string PackageName = "media-server");

public sealed record BrandingConfiguration(
    string LoginDisclaimer = "",
    string CustomCss = "",
    bool SplashscreenEnabled = false);

public sealed record AuthenticationResultDto(
    UserDto User,
    SessionInfoDto SessionInfo,
    string AccessToken,
    string ServerId);

public sealed record UserDto(
    string Name,
    string ServerId,
    string Id,
    bool HasPassword,
    bool HasConfiguredPassword,
    UserConfigurationDto Configuration,
    UserPolicyDto Policy,
    bool HasConfiguredEasyPassword = false,
    bool EnableAutoLogin = false,
    DateTimeOffset? LastLoginDate = null,
    DateTimeOffset? LastActivityDate = null);

public sealed record UserConfigurationDto(
    bool PlayDefaultAudioTrack = true,
    bool DisplayMissingEpisodes = false,
    string[]? GroupedFolders = null,
    bool EnableNextEpisodeAutoPlay = true);

public sealed record UserPolicyDto(
    bool IsAdministrator,
    bool IsDisabled = false,
    bool IsHidden = false,
    bool EnableAllFolders = true,
    bool EnableMediaPlayback = true,
    bool EnableContentDownloading = true,
    bool EnableRemoteAccess = true,
    string[]? EnabledFolders = null,
    string AuthenticationProviderId = "MediaServer");

public sealed record SessionInfoDto(
    string Id,
    string UserId,
    string UserName,
    string? Client,
    string? DeviceName,
    string? DeviceId,
    string? ApplicationVersion,
    string ServerId);

public sealed record UserItemDataDto(
    string Key,
    long PlaybackPositionTicks = 0,
    int PlayCount = 0,
    bool IsFavorite = false,
    bool Played = false,
    double? PlayedPercentage = null,
    DateTimeOffset? LastPlayedDate = null,
    int? UnplayedItemCount = null);

public sealed record BaseItemDto
{
    public required string Id { get; init; }
    public required string ServerId { get; init; }
    public required string Name { get; init; }
    public string? OriginalTitle { get; init; }
    public string? SortName { get; init; }
    public required string Type { get; init; }
    public string? CollectionType { get; init; }
    public string? MediaType { get; init; }
    public bool IsFolder { get; init; }
    public string? ParentId { get; init; }
    public string? SeriesId { get; init; }
    public string? SeriesName { get; init; }
    public string? SeasonId { get; init; }
    public string? SeasonName { get; init; }
    public int? IndexNumber { get; init; }
    public int? IndexNumberEnd { get; init; }
    public int? ParentIndexNumber { get; init; }
    public int? ProductionYear { get; init; }
    public DateTimeOffset? PremiereDate { get; init; }
    public long? RunTimeTicks { get; init; }
    public string? Overview { get; init; }
    public IReadOnlyList<string>? Genres { get; init; }
    public string? OfficialRating { get; init; }
    public double? CommunityRating { get; init; }
    public string LocationType { get; init; } = "FileSystem";
    public string? Container { get; init; }
    public DateTimeOffset? DateCreated { get; init; }
    public int? ChildCount { get; init; }
    public int? RecursiveItemCount { get; init; }
    public string PlayAccess { get; init; } = "Full";
    public IReadOnlyDictionary<string, string>? ImageTags { get; init; }
    public IReadOnlyList<string>? BackdropImageTags { get; init; }
    public IReadOnlyDictionary<string, string>? ProviderIds { get; init; }
    public UserItemDataDto? UserData { get; init; }
    public IReadOnlyList<MediaSourceInfo>? MediaSources { get; init; }
}

public sealed record MediaSourceInfo
{
    public string Protocol { get; init; } = "File";
    public required string Id { get; init; }
    public string? Name { get; init; }
    public required string Container { get; init; }
    public long Size { get; init; }
    public bool IsRemote { get; init; }
    public long? RunTimeTicks { get; init; }
    public bool SupportsTranscoding { get; init; }
    public bool SupportsDirectStream { get; init; } = true;
    public bool SupportsDirectPlay { get; init; } = true;
    public bool IsInfiniteStream { get; init; }
    public bool RequiresOpening { get; init; }
    public bool RequiresClosing { get; init; }
    public bool RequiresLooping { get; init; }
    public bool SupportsProbing { get; init; } = true;
    public string? VideoType { get; init; } = "VideoFile";
    public string Type { get; init; } = "Default";
    public int? Bitrate { get; init; }
    public string? DirectStreamUrl { get; init; }
    public string? ETag { get; init; }
    public IReadOnlyList<MediaStreamDto> MediaStreams { get; init; } = [];
    public IReadOnlyList<string> Formats { get; init; } = [];
    public IReadOnlyList<object> MediaAttachments { get; init; } = [];
    public int? DefaultAudioStreamIndex { get; init; }
    public int? DefaultSubtitleStreamIndex { get; init; }
}

public sealed record MediaStreamDto
{
    public required string Type { get; init; }
    public int Index { get; init; }
    public string? Codec { get; init; }
    public string? Language { get; init; }
    public string? DisplayTitle { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public bool IsExternal { get; init; }
    public string? Profile { get; init; }
    public int? Height { get; init; }
    public int? Width { get; init; }
    public double? AverageFrameRate { get; init; }
    public double? RealFrameRate { get; init; }
    public int? BitDepth { get; init; }
    public string? VideoRange { get; init; }
    public string? AspectRatio { get; init; }
    public int? Channels { get; init; }
    public int? SampleRate { get; init; }
    public string? ChannelLayout { get; init; }
    public bool? IsTextSubtitleStream { get; init; }
    public bool SupportsExternalStream { get; init; }
    public string? DeliveryMethod { get; init; }
}

public sealed record PlaybackInfoResponse(
    IReadOnlyList<MediaSourceInfo> MediaSources,
    string PlaySessionId,
    string? ErrorCode = null);

public sealed record PlaybackInfoRequest(
    string? UserId,
    string? MediaSourceId,
    bool? EnableDirectPlay,
    bool? EnableDirectStream,
    int? AudioStreamIndex,
    int? SubtitleStreamIndex);

public sealed record AuthenticateByNameRequest(string? Username, string? Pw, string? Password);

/// <summary>
/// Shared body for the <c>Sessions/Playing*</c> reports (start, progress, stopped). Only the fields the
/// resume/watched policy needs are modeled; clients may also pass them on the query string.
/// </summary>
public sealed record PlaybackReportBody(
    string? ItemId,
    long? PositionTicks,
    string? MediaSourceId,
    string? PlaySessionId,
    bool? IsPaused);

public sealed record SpecialViewOptionDto(string Name, string Id);
