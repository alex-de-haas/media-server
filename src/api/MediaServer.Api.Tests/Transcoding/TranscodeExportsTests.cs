using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Transcoding;

namespace MediaServer.Api.Tests.Transcoding;

public sealed class TranscodeExportsTests : IDisposable
{
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "ms-exports-" + Guid.NewGuid().ToString("N"));
    public TranscodeExportsTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private TranscodeExports Service(string? output = null, string label = "results") => new(new MediaServerSettings
    {
        CatalogMountRoots = [new("media", Path.Combine(root, "library"))],
        TranscodeOutputRoots = [new(label, output ?? Path.Combine(root, "outputs"))],
    });

    [Theory]
    [InlineData("../outside.mkv")]
    [InlineData("/outside.mkv")]
    [InlineData("nested/file.mkv")]
    [InlineData("nested\\\\file.mkv")]
    public void Rejects_unowned_filename(string name)
    {
        Directory.CreateDirectory(Path.Combine(root, "outputs"));
        Assert.Throws<TranscodeRequestException>(() => Service().Resolve(Guid.NewGuid(), "results", name));
    }

    [Fact]
    public void Filesystem_root_overlaps_every_descendant()
    {
        var service = new TranscodeExports(new() { CatalogMountRoots = [new("media", Path.GetPathRoot(root)!)],
            TranscodeOutputRoots = [new("results", root)] });
        Assert.Empty(service.Destinations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Catalog_side_alias_cannot_hide_overlapping_output(bool ancestor)
    {
        var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var alias = Path.Combine(root, "alias"); Directory.CreateSymbolicLink(alias, library);
        var service = new TranscodeExports(new() {
            CatalogMountRoots = [new("media", ancestor ? Path.Combine(alias, "nested") : alias)],
            TranscodeOutputRoots = [new("results", library)] });
        try { Assert.Empty(service.Destinations); }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public void Rejects_overlapping_mounts_and_shared_labels()
    {
        Assert.Empty(Service(Path.Combine(root, "library", "out")).Destinations);
        Assert.Empty(Service(root).Destinations);
        Assert.Empty(Service(label: "media").Destinations);
    }

    [Fact]
    public void Rejects_symlink_ancestor_even_when_final_file_exists()
    {
        var output = Path.Combine(root, "outputs"); Directory.CreateDirectory(output);
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "film.mkv"), "original");
        var id = Guid.NewGuid();
        var link = Path.Combine(output, id.ToString("N"));
        Directory.CreateSymbolicLink(link, outside);
        try { Assert.Throws<TranscodeRequestException>(() => Service().Resolve(id, "results", "film.mkv")); }
        finally { Directory.Delete(link); }
        Assert.Equal("original", File.ReadAllText(Path.Combine(outside, "film.mkv")));
    }

    [Fact]
    public void Cannot_resolve_another_jobs_directory()
    {
        Directory.CreateDirectory(Path.Combine(root, "outputs"));
        var job = new TranscodeJob { Id = Guid.NewGuid(), EngineJobId = "test", VideoCodec = "copy", HardwareAcceleration = "none", InputPath = "original.mkv",
            OutputMountLabel = "results", OutputPath = Guid.NewGuid().ToString("N") + "/film.mkv" };
        Assert.Throws<TranscodeRequestException>(() => Service().Resolve(job));
    }
}
