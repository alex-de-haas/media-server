namespace MediaServer.Api.Data;

/// <summary>
/// The pipeline state machine for one acquired download. The orchestrator advances
/// <see cref="Stage"/>/<see cref="Status"/>, records <see cref="StagesCompleted"/> for resume, claims
/// the item with a lease (<see cref="LeaseOwner"/>/<see cref="LeaseUntil"/>), and uses
/// <see cref="RowVersion"/> as an optimistic-concurrency token so the reconciler and operator actions
/// never double-drive the same item.
/// </summary>
public sealed class IngestItem
{
    public Guid Id { get; set; }

    public Guid CatalogId { get; set; }

    /// <summary>Null for future ACQ-originated items.</summary>
    public Guid? DownloadId { get; set; }

    /// <summary>Set on publish.</summary>
    public Guid? MediaItemId { get; set; }

    public IngestStage Stage { get; set; }

    public IngestStatus Status { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Resume point on re-entry; JSON column of completed stage keys.</summary>
    public List<string> StagesCompleted { get; set; } = new();

    /// <summary>Single-flight claim owner; null when unclaimed.</summary>
    public string? LeaseOwner { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Earliest time the reconciler may re-drive this item (backoff after Deferred/retry).</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Optimistic-concurrency token, bumped on every persisted change.</summary>
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();

    /// <summary>Provider candidates (JSON) when <see cref="Status"/> is NeedsReview.</summary>
    public string? ReviewCandidates { get; set; }

    public string? LastError { get; set; }

    /// <summary>OpenTelemetry trace id for linking a UI activity row to its trace.</summary>
    public string? TraceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Catalog? Catalog { get; set; }

    public Download? Download { get; set; }
}
