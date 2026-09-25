using MediaServer.Api.Bluray;

namespace MediaServer.Api.Tests.Bluray;

public sealed class BlurayPathsTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "bluray-paths-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() => Directory.Delete(root, true);
    private void Write(string path)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "disc");
    }

    [Fact]
    public void Organization_and_deletion_preserve_unowned_siblings()
    {
        Write("source/BDMV/index.bdmv"); Write("source/CERTIFICATE/id.bdmv"); Write("source/another.mkv");
        var source = Path.Combine(root, "source"); var target = Path.Combine(root, "target");
        BlurayPaths.MoveMembers(source, target);
        Assert.True(File.Exists(Path.Combine(source, "another.mkv")));
        Assert.True(File.Exists(Path.Combine(target, "CERTIFICATE/id.bdmv")));
        Write("target/unowned.txt");
        BlurayPaths.Delete(target);
        Assert.True(File.Exists(Path.Combine(target, "unowned.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "BDMV")));
    }

    [Fact]
    public void Organization_recovers_after_one_member_was_moved()
    {
        Write("source/BDMV/index.bdmv"); Write("source/CERTIFICATE/id.bdmv");
        var source = Path.Combine(root, "source"); var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        Directory.Move(Path.Combine(source, "BDMV"), Path.Combine(target, "BDMV"));
        BlurayPaths.MoveMembers(source, target);
        Assert.True(File.Exists(Path.Combine(target, "CERTIFICATE/id.bdmv")));
        Assert.True(BlurayPaths.IsDisc(target));
    }

    [Fact]
    public void Symlinked_destination_is_refused_before_moving_members()
    {
        Write("source/BDMV/index.bdmv");
        Directory.CreateDirectory(Path.Combine(root, "outside"));
        Directory.CreateSymbolicLink(Path.Combine(root, "target"), Path.Combine(root, "outside"));
        Assert.Throws<IOException>(() => BlurayPaths.MoveMembers(Path.Combine(root, "source"), Path.Combine(root, "target/disc")));
        Assert.True(File.Exists(Path.Combine(root, "source/BDMV/index.bdmv")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "outside")));
    }

    [Fact]
    public void Symlinked_members_are_refused_before_mutation()
    {
        Write("source/BDMV/index.bdmv"); Write("outside/important.txt");
        Directory.CreateSymbolicLink(Path.Combine(root, "source/BDMV/STREAM"), Path.Combine(root, "outside"));
        Assert.Throws<IOException>(() => BlurayPaths.MoveMembers(Path.Combine(root, "source"), Path.Combine(root, "target")));
        Assert.Throws<IOException>(() => BlurayPaths.Delete(Path.Combine(root, "source")));
        Assert.True(File.Exists(Path.Combine(root, "outside/important.txt")));
    }
}
