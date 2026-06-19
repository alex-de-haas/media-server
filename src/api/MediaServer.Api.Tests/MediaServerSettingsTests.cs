using MediaServer.Api.Configuration;

namespace MediaServer.Api.Tests;

public sealed class MediaServerSettingsTests
{
    [Fact]
    public void ParseMountRoots_returns_empty_for_missing_value()
    {
        Assert.Empty(MediaServerSettings.ParseMountRoots(null));
        Assert.Empty(MediaServerSettings.ParseMountRoots(""));
        Assert.Empty(MediaServerSettings.ParseMountRoots("   "));
    }

    [Fact]
    public void ParseMountRoots_keeps_a_single_path()
    {
        Assert.Equal(["/Users/haas/dev-media"], MediaServerSettings.ParseMountRoots("/Users/haas/dev-media"));
    }

    [Fact]
    public void ParseMountRoots_splits_comma_joined_paths()
    {
        // Core joins multiple mounts with a comma into HOSTY_MOUNT_CATALOGROOTS (and forbids commas
        // in mount host paths), so the parser must split on it for multi-mount setups.
        Assert.Equal(
            ["/mnt/catalogRoots/movies", "/mnt/catalogRoots/anime"],
            MediaServerSettings.ParseMountRoots("/mnt/catalogRoots/movies,/mnt/catalogRoots/anime"));
    }

    [Fact]
    public void ParseMountRoots_strips_optional_label_prefix_and_dedupes()
    {
        Assert.Equal(
            ["/mnt/a", "/mnt/b"],
            MediaServerSettings.ParseMountRoots("media=/mnt/a,/mnt/b,/mnt/a"));
    }
}
