namespace MediaServer.Api.Data;

/// <summary>
/// Observability record for a unit of background work. Pipeline stages emit jobs so the UI can show
/// the full flow per item. Torrent download progress is the exception — it is broadcast live and
/// never persisted here.
/// </summary>
public sealed class Job
{
    public Guid Id { get; set; }

    public required string Type { get; set; }

    public string? RelatedType { get; set; }

    public Guid? RelatedId { get; set; }

    public JobStatus Status { get; set; }

    /// <summary>0–100.</summary>
    public int Progress { get; set; }

    public int AttemptCount { get; set; }

    public string? Error { get; set; }

    /// <summary>OpenTelemetry trace id, linking the row to its trace.</summary>
    public string? TraceId { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
