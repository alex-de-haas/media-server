using MediaServer.Api.Configuration;
using Microsoft.Extensions.Configuration;

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
    public void ParseMountRoots_blank_explicit_label_falls_back_to_base_name()
    {
        // A malformed `=/path` entry has a blank explicit label; derive it from the base name rather than
        // forwarding an empty mountLabel to the engine.
        Assert.Equal(
            [("anime", "/mnt/anime")],
            MediaServerSettings.ParseMountRoots("=/mnt/anime").Select(mount => (mount.Label, mount.Path)));
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

    [Fact]
    public void WatchRegion_defaults_to_US_and_is_independent_of_supported_languages()
    {
        // WATCH_REGION is its own axis: ru-RU metadata languages must not move the watch region.
        var settings = MediaServerSettings.FromConfiguration(Configuration(("SUPPORTED_LANGUAGES", "ru-RU")));
        Assert.Equal("US", settings.WatchRegion);
        Assert.Equal(["ru-RU"], settings.SupportedLanguages);
    }

    [Fact]
    public void WatchRegion_reads_and_normalizes_the_setting()
    {
        Assert.Equal("RU", MediaServerSettings.FromConfiguration(Configuration(("WATCH_REGION", "ru"))).WatchRegion);
        Assert.Equal("DE", MediaServerSettings.FromConfiguration(Configuration(("WATCH_REGION", " DE "))).WatchRegion);
    }

    [Fact]
    public void WatchRegion_falls_back_to_US_for_an_invalid_value()
    {
        Assert.Equal("US", MediaServerSettings.FromConfiguration(Configuration(("WATCH_REGION", "USA"))).WatchRegion);
        Assert.Equal("US", MediaServerSettings.FromConfiguration(Configuration(("WATCH_REGION", "1x"))).WatchRegion);
        Assert.Equal("US", MediaServerSettings.FromConfiguration(Configuration(("WATCH_REGION", ""))).WatchRegion);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();
}
