using MediaServer.Api.Data;
using MediaServer.Api.Torrents;

namespace MediaServer.Api.Tests.Torrents;

/// <summary>
/// Covers the progress semantics of <see cref="DownloadResponse.From"/>: a completed download is 100%
/// by definition, even though the live engine snapshot reports a stale sub-100 value after a no-seed
/// organize unlinks the seed copy (or no snapshot at all once the stopped manager is gone on restart).
/// </summary>
public sealed class DownloadResponseTests
{
    [Theory]
    [InlineData(DownloadState.Completed)]
    [InlineData(DownloadState.Seeding)]
    [InlineData(DownloadState.StoppedSeeding)]
    public void From_reports_100_percent_for_completed_states_despite_stale_snapshot(DownloadState state)
    {
        var download = NewDownload(state);
        // The engine still has a stopped manager whose seed copy was unlinked: a stale 94.1%.
        var snapshot = NewSnapshot(percentComplete: 94.1, complete: false);

        var response = DownloadResponse.From(download, snapshot);

        Assert.Equal(100, response.PercentComplete);
    }

    [Theory]
    [InlineData(DownloadState.Completed)]
    [InlineData(DownloadState.Seeding)]
    [InlineData(DownloadState.StoppedSeeding)]
    public void From_reports_100_percent_for_completed_states_when_no_snapshot(DownloadState state)
    {
        // After a restart a StoppedSeeding download is not resumed, so the engine has no manager for it.
        var response = DownloadResponse.From(NewDownload(state), snapshot: null);

        Assert.Equal(100, response.PercentComplete);
    }

    [Fact]
    public void From_passes_through_live_progress_for_in_flight_downloads()
    {
        var response = DownloadResponse.From(
            NewDownload(DownloadState.Downloading), NewSnapshot(percentComplete: 42.5, complete: false));

        Assert.Equal(42.5, response.PercentComplete);
    }

    [Fact]
    public void From_leaves_progress_null_for_in_flight_download_without_snapshot()
    {
        var response = DownloadResponse.From(NewDownload(DownloadState.Downloading), snapshot: null);

        Assert.Null(response.PercentComplete);
    }

    private static Download NewDownload(DownloadState state) => new()
    {
        Id = Guid.NewGuid(),
        InfoHash = "abc",
        Name = "Movie",
        CatalogId = Guid.NewGuid(),
        SourceType = TorrentSourceType.Magnet,
        State = state,
        KeepSeeding = false,
        SavePath = "/tmp/files",
        AddedAt = DateTimeOffset.UtcNow,
    };

    private static TorrentSnapshot NewSnapshot(double percentComplete, bool complete) => new(
        InfoHash: "abc",
        Name: "Movie",
        EngineState: "Stopped",
        Complete: complete,
        PercentComplete: percentComplete,
        DownloadRateBytesPerSecond: 0,
        UploadRateBytesPerSecond: 0,
        Ratio: 0,
        Peers: 0,
        SizeBytes: 1000);
}
