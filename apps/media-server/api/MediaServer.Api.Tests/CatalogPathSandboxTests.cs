using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;

namespace MediaServer.Api.Tests;

public sealed class CatalogPathSandboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-sandbox-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogPathSandbox _sandbox = new();
    private readonly Catalog _catalog;

    public CatalogPathSandboxTests()
    {
        Directory.CreateDirectory(_root);
        _catalog = new Catalog { Name = "Test", Root = _root, Type = CatalogType.Movie };
    }

    [Fact]
    public void Resolves_contained_relative_path()
    {
        var resolved = _sandbox.TryResolve(_catalog, "library/Inception (2010)/Inception (2010).mkv", out var absolute);

        Assert.True(resolved);
        Assert.StartsWith(Path.GetFullPath(_root), absolute);
        Assert.EndsWith("Inception (2010).mkv", absolute);
    }

    [Theory]
    [InlineData("../escape.mkv")]
    [InlineData("library/../../escape.mkv")]
    [InlineData("../../etc/passwd")]
    public void Rejects_parent_traversal(string relativePath)
    {
        Assert.False(_sandbox.TryResolve(_catalog, relativePath, out _));
    }

    [Fact]
    public void Rejects_absolute_path()
    {
        Assert.False(_sandbox.TryResolve(_catalog, "/etc/passwd", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_blank_path(string relativePath)
    {
        Assert.False(_sandbox.TryResolve(_catalog, relativePath, out _));
    }

    [Fact]
    public void Rejects_symlink_escape()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Symlink creation needs elevation on Windows; the POSIX runtimes cover this path.
        }

        var outside = Path.Combine(Path.GetTempPath(), "ms-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var linkPath = Path.Combine(_root, "escape-link");
            Directory.CreateSymbolicLink(linkPath, outside);

            // Lexically contained, but the symlink target escapes the root → rejected.
            Assert.False(_sandbox.TryResolve(_catalog, "escape-link/secret.mkv", out _));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
