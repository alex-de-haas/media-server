using MediaServer.Api.Data;

namespace MediaServer.Api.Bluray;

/// <summary>Disc ownership is limited to BDMV and its companion CERTIFICATE tree, never the enclosing catalog.</summary>
public static class BlurayPaths
{
    public static string? RootOfMember(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(parts, p => p.Equals("BDMV", StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? string.Join('/', parts.Take(index)) : null;
    }

    public static bool IsMember(string path, string root)
    {
        var relative = path.Replace('\\', '/');
        if (root.Length > 0)
        {
            if (!relative.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return false;
            relative = relative[(root.Length + 1)..];
        }
        var top = relative.Split('/')[0];
        return top.Equals("BDMV", StringComparison.OrdinalIgnoreCase) || top.Equals("CERTIFICATE", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDisc(string path) => Directory.Exists(path) &&
        Directory.EnumerateDirectories(path).Any(p => Path.GetFileName(p).Equals("BDMV", StringComparison.OrdinalIgnoreCase));

    public static bool Exists(string path) => File.Exists(path) || IsDisc(path);

    public static IEnumerable<string> Members(string root) => Directory.EnumerateDirectories(root)
        .Where(p => Path.GetFileName(p).Equals("BDMV", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(p).Equals("CERTIFICATE", StringComparison.OrdinalIgnoreCase));

    public static void ValidateAncestors(string path)
    {
        for (DirectoryInfo? parent = new(path); parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null) throw new IOException("Blu-ray paths cannot contain symbolic links.");
    }

    public static void ValidateTree(string root)
    {
        ValidateAncestors(root);
        var stack = new Stack<string>(Members(root));
        while (stack.TryPop(out var current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Blu-ray members cannot be symbolic links.");
            if (Directory.Exists(current))
                foreach (var entry in Directory.EnumerateFileSystemEntries(current)) stack.Push(entry);
        }
    }

    public static void ValidateStructure(string root)
    {
        ValidateTree(root);
        var bdmv = Members(root).SingleOrDefault(p => Path.GetFileName(p).Equals("BDMV", StringComparison.OrdinalIgnoreCase));
        if (bdmv is null || !Directory.EnumerateFiles(bdmv).Any(p => Path.GetFileName(p).Equals("index.bdmv", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The Blu-ray structure is incomplete: index.bdmv is missing.");
        foreach (var name in new[] { "STREAM", "PLAYLIST", "CLIPINF" })
            if (!Directory.EnumerateDirectories(bdmv).Any(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase) && Directory.EnumerateFiles(p).Any()))
                throw new IOException($"The Blu-ray structure is incomplete: {name} is empty or missing.");
    }

    public static long Size(string path)
    {
        if (!IsDisc(path)) return new FileInfo(path).Length;
        ValidateTree(path);
        return Members(path).Sum(member => Directory.EnumerateFiles(member, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length));
    }

    /// <summary>Resumes a persisted organization intent without moving unrelated siblings.</summary>
    public static void MoveMembers(string source, string destination)
    {
        ValidateTree(source);
        ValidateAncestors(destination);
        Directory.CreateDirectory(destination);
        foreach (var member in Members(source).ToArray())
        {
            var target = Path.Combine(destination, Path.GetFileName(member));
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("A Blu-ray destination member already exists.");
            Directory.Move(member, target);
        }
        if (!IsDisc(destination)) throw new IOException("The organized Blu-ray is missing its BDMV directory.");
    }

    public static void Move(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            MoveMembers(source, destination);
            if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
        }
        else File.Move(source, destination);
    }

    public static void Delete(string path)
    {
        if (!Directory.Exists(path)) { if (File.Exists(path)) File.Delete(path); return; }
        ValidateTree(path);
        foreach (var member in Members(path).ToArray()) Directory.Delete(member, recursive: true);
        if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }

    public static bool IsImportable(SourceFile source) => source.Kind == MediaSourceKind.Bluray ||
        Media.MediaFormats.IsPlayableMedia(source.RelativePath, source.SizeBytes);
}
