using MediaServer.Api.Torrents;

namespace MediaServer.Api.Tests.Torrents;

public sealed class RemoteTorrentEngineTests
{
    [Fact]
    public void ToMountRelative_UnderMountRoot_ReturnsIncomingRelativePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "catalogs", "media");
        var save = Path.Combine(root, ".incoming", "abc123");

        var relative = RemoteTorrentEngine.ToMountRelative(save, [root]);

        Assert.Equal(".incoming/abc123", relative);
    }

    [Fact]
    public void ToMountRelative_CatalogSubdirectoryUnderMount_PreservesSubdirectory()
    {
        // Mount root is the shared host path; the catalog sits in a subdirectory of it.
        var mount = Path.Combine(Path.GetTempPath(), "mnt", "catalogRoots");
        var save = Path.Combine(mount, "media", ".incoming", "abc");

        var relative = RemoteTorrentEngine.ToMountRelative(save, [mount]);

        Assert.Equal("media/.incoming/abc", relative);
    }

    [Fact]
    public void ToMountRelative_NoMatchingMount_FallsBackToTrailingSegments()
    {
        var save = Path.Combine(Path.GetTempPath(), "somewhere", ".incoming", "abc");

        var relative = RemoteTorrentEngine.ToMountRelative(save, [Path.Combine(Path.GetTempPath(), "other", "root")]);

        Assert.Equal(".incoming/abc", relative);
    }
}
