using System.Runtime.InteropServices;

namespace MediaServer.Api.IO;

/// <summary>
/// Creates hardlinks between two paths on the same filesystem. The organizer relies on hardlinks so
/// the seed copy in <c>files/</c> and the clean entry in <c>library/</c> share one inode (zero copy);
/// see <c>docs/planning/torrents-and-organizer.md</c>.
/// </summary>
public interface IHardLinker
{
    /// <summary>Creates a hardlink <paramref name="linkPath"/> pointing at <paramref name="existingPath"/>.</summary>
    void Create(string existingPath, string linkPath);

    /// <summary>
    /// True when two directories are on the same filesystem (a hardlink can span them). Implemented by
    /// probing an actual hardlink, which is exactly the capability the organizer needs.
    /// </summary>
    bool AreSameFilesystem(string directoryA, string directoryB);
}

public sealed partial class HardLinker : IHardLinker
{
    public void Create(string existingPath, string linkPath)
    {
        var directory = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkW(linkPath, existingPath, IntPtr.Zero))
            {
                throw new IOException($"CreateHardLink failed ({Marshal.GetLastPInvokeError()}): {existingPath} -> {linkPath}");
            }

            return;
        }

        if (link(existingPath, linkPath) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"link() failed (errno {error}): {existingPath} -> {linkPath}", new System.ComponentModel.Win32Exception(error));
        }
    }

    public bool AreSameFilesystem(string directoryA, string directoryB)
    {
        Directory.CreateDirectory(directoryA);
        Directory.CreateDirectory(directoryB);

        var probeSource = Path.Combine(directoryA, $".hostylink-probe-{Guid.NewGuid():N}");
        var probeLink = Path.Combine(directoryB, $".hostylink-probe-{Guid.NewGuid():N}");

        try
        {
            File.WriteAllBytes(probeSource, []);
            Create(probeSource, probeLink);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            TryDelete(probeLink);
            TryDelete(probeSource);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort: probe artifacts are tiny and harmless if a transient error leaves one behind.
        }
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int link(string oldpath, string newpath);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
