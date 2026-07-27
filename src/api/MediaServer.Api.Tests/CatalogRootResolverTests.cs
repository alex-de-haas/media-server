using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;

namespace MediaServer.Api.Tests;

/// <summary>
/// The label↔path translation that makes a catalog survive a runtime switch: Hosty injects host paths
/// for a mount under the dev runtime and container paths under docker, so only the label is portable.
/// </summary>
public sealed class CatalogRootResolverTests
{
    private static readonly IReadOnlyList<CatalogMount> DevMounts =
    [
        new("dev_media_1", "/Users/haas/dev-media"),
        new("dev_media_2", "/Users/haas/dev-media-2"),
    ];

    private static readonly IReadOnlyList<CatalogMount> DockerMounts =
    [
        new("dev_media_1", "/mnt/catalogRoots/dev_media_1"),
        new("dev_media_2", "/mnt/catalogRoots/dev_media_2"),
    ];

    [Fact]
    public void Round_trips_a_root_between_runtime_profiles()
    {
        // Created under dev…
        var anchor = CatalogRootResolver.ToMountRelative(DevMounts, "/Users/haas/dev-media/movies");

        Assert.NotNull(anchor);
        Assert.Equal("dev_media_1", anchor.Value.Label);
        Assert.Equal("movies", anchor.Value.Relative);

        // …resolves to the container path under docker, and back again.
        Assert.Equal(
            "/mnt/catalogRoots/dev_media_1/movies",
            CatalogRootResolver.Resolve(DockerMounts, anchor.Value.Label, anchor.Value.Relative));
        Assert.Equal(
            "/Users/haas/dev-media/movies",
            CatalogRootResolver.Resolve(DevMounts, anchor.Value.Label, anchor.Value.Relative));
    }

    [Fact]
    public void Handles_a_catalog_at_the_mount_root_itself()
    {
        var anchor = CatalogRootResolver.ToMountRelative(DevMounts, "/Users/haas/dev-media");

        Assert.NotNull(anchor);
        Assert.Equal(string.Empty, anchor.Value.Relative);
        Assert.Equal("/mnt/catalogRoots/dev_media_1", CatalogRootResolver.Resolve(DockerMounts, "dev_media_1", ""));
    }

    [Fact]
    public void Resolves_nested_paths_and_picks_the_right_mount()
    {
        Assert.Equal(
            "/mnt/catalogRoots/dev_media_2/tv/anime",
            CatalogRootResolver.Resolve(DockerMounts, "dev_media_2", "tv/anime"));

        var anchor = CatalogRootResolver.ToMountRelative(DevMounts, "/Users/haas/dev-media-2/tv/anime");
        Assert.Equal("dev_media_2", anchor!.Value.Label);
        Assert.Equal("tv/anime", anchor.Value.Relative);
    }

    [Fact]
    public void A_sibling_directory_sharing_a_prefix_is_not_inside_the_mount()
    {
        // "/Users/haas/dev-media-2" starts with "/Users/haas/dev-media" as a string, but is a different
        // directory — containment has to be checked segment-wise.
        var anchor = CatalogRootResolver.ToMountRelative([new CatalogMount("dev_media_1", "/Users/haas/dev-media")], "/Users/haas/dev-media-2/tv");

        Assert.Null(anchor);
    }

    [Fact]
    public void Returns_null_for_a_label_this_runtime_does_not_provide()
    {
        Assert.Null(CatalogRootResolver.Resolve(DockerMounts, "archive", "movies"));
        Assert.Null(CatalogRootResolver.Resolve(DockerMounts, null, "movies"));
        Assert.Null(CatalogRootResolver.Resolve([], "dev_media_1", "movies"));
    }

    [Fact]
    public void Matches_labels_case_insensitively()
    {
        // A casing difference between what Core injects and what was stored must not unanchor a catalog.
        Assert.Equal("/mnt/catalogRoots/dev_media_1/movies", CatalogRootResolver.Resolve(DockerMounts, "DEV_MEDIA_1", "movies"));
    }

    [Fact]
    public void Refuses_a_relative_path_that_escapes_its_mount()
    {
        Assert.Null(CatalogRootResolver.Resolve(DockerMounts, "dev_media_1", "../../etc"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(".", "")]
    [InlineData("/movies/", "movies")]
    [InlineData("  movies  ", "movies")]
    [InlineData("tv\\anime", "tv/anime")]
    public void Normalizes_relative_paths(string? input, string expected) =>
        Assert.Equal(expected, CatalogRootResolver.Normalize(input));
}
