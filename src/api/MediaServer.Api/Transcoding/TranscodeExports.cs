using MediaServer.Api.Configuration;
using MediaServer.Api.Data;

namespace MediaServer.Api.Transcoding;

/// <summary>Explicit export mounts own one directory per job; exports never become catalog-relative sources.</summary>
public sealed class TranscodeExports(MediaServerSettings settings)
{
    public IReadOnlyList<string> Destinations => settings.TranscodeOutputRoots
        .Where(IsSeparate).Select(mount => mount.Label).ToList();

    public (string Label, string Relative, string Absolute) Resolve(Guid jobId, string label, string fileName)
    {
        var mount = settings.TranscodeOutputRoots.SingleOrDefault(candidate =>
            string.Equals(candidate.Label, label, StringComparison.Ordinal));
        if (mount is null || !IsSeparate(mount))
            throw new TranscodeRequestException("Choose a configured output mount separate from every catalog mount.");
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ||
            fileName.IndexOfAny(['/', '\\']) >= 0 || Path.IsPathRooted(fileName))
            throw new TranscodeRequestException("Invalid export filename.");
        var root = Path.GetFullPath(mount.Path);
        if (!Directory.Exists(root)) throw new TranscodeRequestException("The output mount is offline.");
        var relative = $"{jobId:N}/{fileName}";
        var absolute = Path.Combine(root, jobId.ToString("N"), fileName);
        // Check every ancestor, not only the final file: an existing regular file can sit below a symlink.
        for (var current = absolute; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new TranscodeRequestException("Export paths cannot contain symbolic links.");
        }
        return (mount.Label, relative, absolute);
    }

    public string Resolve(TranscodeJob job)
    {
        if (job.OutputMountLabel is null || job.OutputPath is null ||
            !job.OutputPath.StartsWith($"{job.Id:N}/", StringComparison.Ordinal) ||
            job.OutputPath.Count(c => c == '/') != 1)
            throw new TranscodeRequestException("The job does not own this export path.");
        return Resolve(job.Id, job.OutputMountLabel, job.OutputPath[(job.OutputPath.IndexOf('/') + 1)..]).Absolute;
    }

    public void Delete(TranscodeJob job)
    {
        var absolute = Resolve(job);
        if (Directory.Exists(absolute)) throw new TranscodeRequestException("An export file was replaced by a directory.");
        File.Delete(absolute);
        var directory = Path.GetDirectoryName(absolute)!;
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    private bool IsSeparate(CatalogMount mount) => !settings.CatalogMountRoots.Any(input =>
        string.Equals(input.Label, mount.Label, StringComparison.OrdinalIgnoreCase) ||
        Contains(input.Path, mount.Path) || Contains(mount.Path, input.Path));

    private static bool Contains(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        return child.Equals(parent, comparison) ||
            child.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }
}
