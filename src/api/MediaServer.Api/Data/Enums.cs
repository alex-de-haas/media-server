namespace MediaServer.Api.Data;

/// <summary>Catalog type drives the name parser, metadata provider, and Jellyfin collection type.</summary>
public enum CatalogType
{
    Movie = 0,
    Series = 1,
    Anime = 2,
}

/// <summary>Unified, hierarchical media kind (matches Jellyfin <c>BaseItem</c> shapes).</summary>
public enum MediaKind
{
    Movie = 0,
    Series = 1,
    Season = 2,
    Episode = 3,
    Video = 4,
}

/// <summary>Persisted torrent lifecycle state. Only transitions are written; progress is not.</summary>
public enum DownloadState
{
    Queued = 0,
    Downloading = 1,
    Completed = 2,
    Seeding = 3,
    StoppedSeeding = 4,
    Stopped = 5,
    Error = 6,
}

/// <summary>How a download was added.</summary>
public enum TorrentSourceType
{
    Magnet = 0,
    File = 1,
}

/// <summary>Persisted transcode-job lifecycle state. Mirrors the external engine's job states; only
/// transitions are written, live progress rides the realtime stream / list snapshot.</summary>
public enum TranscodeJobState
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>The v1 processing (PROC) pipeline stages, persisted as <c>IngestItem.Stage</c>.</summary>
public enum IngestStage
{
    Intake = 0,
    Identify = 1,
    Download = 2,
    Organize = 3,
    Probe = 4,
    Enrich = 5,
    Publish = 6,
}

/// <summary>High-level status of an ingest item in the orchestrator.</summary>
public enum IngestStatus
{
    Pending = 0,
    Running = 1,
    NeedsReview = 2,
    Failed = 3,
    Done = 4,
}

/// <summary>Pipeline phase; acquisition stages (M5) sort before processing stages.</summary>
public enum PipelinePhase
{
    Acquisition = 0,
    Processing = 1,
}

public enum StreamType
{
    Video = 0,
    Audio = 1,
    Subtitle = 2,
}

public enum ImageType
{
    Primary = 0,
    Backdrop = 1,
    Logo = 2,
}

/// <summary>Assignment status of a playable source file to a movie/episode.</summary>
public enum SourceFileAssignmentStatus
{
    Unassigned = 0,
    Suggested = 1,
    Confirmed = 2,
    NeedsReview = 3,
}

/// <summary>Status of an observable background <see cref="Job"/>.</summary>
public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>How a <see cref="Person"/> is credited on a media item (cast = acting, crew = everyone else).</summary>
public enum PersonRole
{
    Cast = 0,
    Crew = 1,
}
