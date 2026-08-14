using MediaServer.Api.Configuration;
using MediaServer.Api.Torrents;

namespace MediaServer.Api.Tests.Torrents;

public sealed class RemoteTorrentEngineTests
{
    [Fact]
    public void ToMountRelative_UnderMountRoot_ReturnsMountLabelAndIncomingRelativePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "catalogs", "media");
        var save = Path.Combine(root, ".incoming", "abc123");

        var (label, relative) = RemoteTorrentEngine.ToMountRelative(save, [new CatalogMount("media", root)]);

        Assert.Equal("media", label);
        Assert.Equal(".incoming/abc123", relative);
    }

    [Fact]
    public void ToMountRelative_CatalogSubdirectoryUnderMount_PreservesSubdirectoryAndLabel()
    {
        // Mount root is the shared host path; the catalog sits in a subdirectory of it.
        var mount = Path.Combine(Path.GetTempPath(), "mnt", "catalogRoots");
        var save = Path.Combine(mount, "media", ".incoming", "abc");

        var (label, relative) = RemoteTorrentEngine.ToMountRelative(save, [new CatalogMount("downloads", mount)]);

        Assert.Equal("downloads", label);
        Assert.Equal("media/.incoming/abc", relative);
    }

    [Fact]
    public void ToMountRelative_PicksTheMatchingMountAmongSeveral()
    {
        var movies = Path.Combine(Path.GetTempPath(), "mnt", "movies");
        var tv = Path.Combine(Path.GetTempPath(), "mnt", "tv");
        var save = Path.Combine(tv, "Anime", ".incoming", "abc");

        var (label, relative) = RemoteTorrentEngine.ToMountRelative(
            save, [new CatalogMount("movies", movies), new CatalogMount("tv", tv)]);

        Assert.Equal("tv", label);
        Assert.Equal("Anime/.incoming/abc", relative);
    }

    [Fact]
    public void ToMountRelative_NoMatchingMount_FallsBackToTrailingSegmentsWithNullLabel()
    {
        var save = Path.Combine(Path.GetTempPath(), "somewhere", ".incoming", "abc");

        var (label, relative) = RemoteTorrentEngine.ToMountRelative(
            save, [new CatalogMount("other", Path.Combine(Path.GetTempPath(), "other", "root"))]);

        Assert.Null(label);
        Assert.Equal(".incoming/abc", relative);
    }

    // ---- DHT status fan-out ----

    private static DhtStatus Dht(bool enabled = true, bool running = true, string? state = "Ready", int nodes = 42) =>
        new(enabled, running, state, nodes);

    [Fact]
    public void IsReportableDhtChange_FirstStatus_IsReported() =>
        Assert.True(RemoteTorrentEngine.IsReportableDhtChange(null, Dht()));

    [Fact]
    public void IsReportableDhtChange_NodeCountChurn_IsNotReported() =>
        // The routing table grows constantly; that alone must not push an event per change.
        Assert.False(RemoteTorrentEngine.IsReportableDhtChange(Dht(nodes: 42), Dht(nodes: 87)));

    [Fact]
    public void IsReportableDhtChange_TableBecomingEmpty_IsReported() =>
        // Running with an empty table is what "enabled but not working" looks like, so this edge matters.
        Assert.True(RemoteTorrentEngine.IsReportableDhtChange(Dht(nodes: 42), Dht(nodes: 0)));

    [Fact]
    public void IsReportableDhtChange_TableFillingUp_IsReported() =>
        Assert.True(RemoteTorrentEngine.IsReportableDhtChange(Dht(nodes: 0), Dht(nodes: 1)));

    [Theory]
    [InlineData("Initialising")]
    [InlineData("NotReady")]
    public void IsReportableDhtChange_StateTransition_IsReported(string state) =>
        // Initialising vs NotReady is the difference between "starting" and "broken" in the UI.
        Assert.True(RemoteTorrentEngine.IsReportableDhtChange(Dht(state: "Ready"), Dht(state: state)));

    [Fact]
    public void IsReportableDhtChange_EngineStoppingRunningDht_IsReported() =>
        Assert.True(RemoteTorrentEngine.IsReportableDhtChange(Dht(), Dht(running: false, state: null, nodes: 0)));

    [Fact]
    public void IsReportableDhtChange_IdenticalStatus_IsNotReported() =>
        Assert.False(RemoteTorrentEngine.IsReportableDhtChange(Dht(), Dht()));
}
