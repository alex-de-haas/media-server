namespace MediaServer.Api.IO;

/// <summary>Filesystem facts the catalog and torrent layers need; abstracted so tests can fake it.</summary>
public interface IFilesystemInspector
{
    bool DirectoryExists(string path);

    bool AreSameFilesystem(string directoryA, string directoryB);

    /// <summary>Bytes available to a non-privileged user on the volume that holds <paramref name="path"/>.</summary>
    long GetAvailableFreeBytes(string path);

    /// <summary>
    /// A stable key identifying the volume that holds <paramref name="path"/> (its mount point), so
    /// catalogs on the same volume can be grouped under one free/total figure. Empty when unknown.
    /// </summary>
    string GetVolumeKey(string path);
}

public sealed class FilesystemInspector(IHardLinker hardLinker) : IFilesystemInspector
{
    // Windows paths are case-insensitive; POSIX paths are not.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool AreSameFilesystem(string directoryA, string directoryB) =>
        hardLinker.AreSameFilesystem(directoryA, directoryB);

    public long GetAvailableFreeBytes(string path) => ResolveDrive(path)?.AvailableFreeSpace ?? 0;

    public string GetVolumeKey(string path) => ResolveDrive(path)?.RootDirectory.FullName ?? string.Empty;

    // DriveInfo is keyed by mount point; pick the longest mount-point prefix of the path so a path on
    // a nested mount resolves to that mount rather than the root volume.
    private static DriveInfo? ResolveDrive(string path)
    {
        var full = Path.GetFullPath(path);
        return DriveInfo.GetDrives()
            .Where(candidate => candidate.IsReady)
            .Where(candidate => full.StartsWith(NormalizeMount(candidate.RootDirectory.FullName), PathComparison))
            .OrderByDescending(candidate => candidate.RootDirectory.FullName.Length)
            .FirstOrDefault();
    }

    private static string NormalizeMount(string mount) =>
        mount.EndsWith(Path.DirectorySeparatorChar) ? mount : mount + Path.DirectorySeparatorChar;
}
