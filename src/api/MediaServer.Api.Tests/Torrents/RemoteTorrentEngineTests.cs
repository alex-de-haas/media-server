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
}
