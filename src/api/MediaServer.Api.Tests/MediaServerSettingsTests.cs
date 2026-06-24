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
    public void ParseMountRoots_keeps_a_single_path_and_derives_its_label_from_the_base_name()
    {
        var mount = Assert.Single(MediaServerSettings.ParseMountRoots("/Users/haas/dev-media"));
        Assert.Equal("/Users/haas/dev-media", mount.Path);
        Assert.Equal("dev-media", mount.Label);
    }

    [Fact]
    public void ParseMountRoots_splits_comma_joined_label_path_entries()
    {
        // Core joins multiple mounts with a comma into HOSTY_MOUNT_CATALOGROOTS as label=path entries
        // (and forbids commas in mount host paths), so the parser splits on comma then the first '='.
        Assert.Equal(
            [("movies", "/mnt/catalogRoots/movies"), ("anime", "/mnt/catalogRoots/anime")],
            MediaServerSettings.ParseMountRoots("movies=/mnt/catalogRoots/movies,anime=/mnt/catalogRoots/anime")
                .Select(mount => (mount.Label, mount.Path)));
    }

    [Fact]
    public void ParseMountRoots_keeps_the_label_from_the_first_equals_and_dedupes_by_path()
    {
        // A host path may itself contain '=', so only the first '=' separates label from path. The second
        // /mnt/a is a duplicate path and is dropped; /mnt/b has no label so it falls back to its base name.
        Assert.Equal(
            [("media", "/mnt/a=x"), ("b", "/mnt/b")],
            MediaServerSettings.ParseMountRoots("media=/mnt/a=x,/mnt/b,other=/mnt/a=x")
                .Select(mount => (mount.Label, mount.Path)));
    }
}
