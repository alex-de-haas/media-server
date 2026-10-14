using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Transcoding;

public sealed partial class TranscodeService
{
    private async Task SubmitExportAsync(TranscodeJob job, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<TranscodeJobRequest>(job.ExportSubmissionJson!)
            ?? throw new InvalidDataException("The export submission record is invalid.");
        job.LastSubmissionAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(ct);
        try
        {
            var descriptor = await engine.CreateAsync(request, ct);
            if (descriptor.JobId != job.EngineJobId)
                throw new HttpRequestException("The engine did not honor the export's client job ID.");
        }
        catch (InvalidOperationException exception)
        {
            job.State = TranscodeJobState.Failed;
            job.Error = exception.Message;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(CancellationToken.None);
            return;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            job.Error = "Submission is unconfirmed; checking the same engine job before retrying.";
            await database.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(exception, "Export {JobId} needs submission confirmation.", job.Id);
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            return;
        }
        job.Error = null;
        // A failed acknowledgement write must not classify an accepted writer as rejected.
        await database.SaveChangesAsync(CancellationToken.None);
    }

    public async Task ReconcileExportAsync(TranscodeJob job, TranscodeOutputImporter importer, CancellationToken ct)
    {
        // Admission, cancellation and deletion share this gate; SSE never promotes exports separately.
        using var mutation = await LibraryFileMutation.EnterAsync(ct);
        await database.Entry(job).ReloadAsync(ct);
        if (database.Entry(job).State == EntityState.Detached || job.OutputImported ||
            job.State is TranscodeJobState.Failed or TranscodeJobState.Cancelled) return;
        try
        {
            if (job.State != TranscodeJobState.Completed)
            {
                var snapshot = await engine.InspectAsync(job.EngineJobId, ct);
                if (snapshot is null && job.ExportSubmissionJson is not null &&
                    (job.CancellationRequested || job.LastSubmissionAt is null ||
                     DateTimeOffset.UtcNow - job.LastSubmissionAt > TimeSpan.FromSeconds(30)))
                {
                    await SubmitExportAsync(job, ct);
                    if (job.State == TranscodeJobState.Failed) return;
                    snapshot = await engine.InspectAsync(job.EngineJobId, ct);
                }
                if (snapshot is null) return;
                if (job.CancellationRequested && snapshot.State is "Queued" or "Running")
                {
                    await engine.CancelAsync(job.EngineJobId, ct);
                    snapshot = await engine.InspectAsync(job.EngineJobId, ct);
                    if (snapshot is null) return;
                }
                if (Enum.TryParse<TranscodeJobState>(snapshot.State, out var state)) job.State = state;
                job.PercentComplete = snapshot.PercentComplete;
                job.Error = snapshot.Error;
                if (job.State is TranscodeJobState.Completed or TranscodeJobState.Failed or TranscodeJobState.Cancelled)
                    job.CompletedAt ??= DateTimeOffset.UtcNow;
                await database.SaveChangesAsync(ct);
            }
            if (job.State == TranscodeJobState.Completed && !job.OutputImported)
            {
                if (!await importer.ImportAsync(job, ct))
                {
                    job.State = TranscodeJobState.Failed;
                    job.Error = "The engine completed but the export is missing or invalid.";
                    await database.SaveChangesAsync(ct);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // An unavailable provider/probe is not evidence that a writer stopped.
            await database.Entry(job).ReloadAsync(ct);
            logger.LogWarning(exception, "Export {JobId} reconciliation needs retry.", job.Id);
        }
    }
}
