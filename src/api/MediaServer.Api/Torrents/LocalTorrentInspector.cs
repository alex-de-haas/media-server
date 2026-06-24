using MonoTorrent;

namespace MediaServer.Api.Torrents;

/// <summary>
/// Pure, offline parsing of a torrent source into its info hash and (for <c>.torrent</c>) size/files.
/// This is the one piece that stays local even when downloading is delegated to a remote engine:
/// the info hash is needed to create the <c>Download</c> row before the torrent is handed off, and a
/// <c>.torrent</c>'s size drives the pre-download free-space refusal. No networking.
/// </summary>
public static class LocalTorrentInspector
{
    public static TorrentDescriptor Inspect(TorrentSource source)
    {
        switch (source)
        {
            case TorrentSource.Magnet magnet:
            {
                if (!MagnetLink.TryParse(magnet.Uri, out var link))
                {
                    throw new ArgumentException("Invalid magnet link.", nameof(source));
                }

                return new TorrentDescriptor(HashOf(link.InfoHashes), link.Name, link.Size, HasMetadata: false, []);
            }

            case TorrentSource.File file:
            {
                var torrent = Torrent.Load(file.Content.AsSpan());
                return new TorrentDescriptor(
                    HashOf(torrent.InfoHashes), torrent.Name, torrent.Size, HasMetadata: true, MapFiles(torrent.Files, torrent.Name));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    public static string HashOf(InfoHashes infoHashes) => infoHashes.V1OrV2.ToHex();

    private static IReadOnlyList<TorrentFileInfo> MapFiles(IList<ITorrentFile> torrentFiles, string torrentName)
    {
        var files = new List<TorrentFileInfo>(torrentFiles.Count);
        for (var index = 0; index < torrentFiles.Count; index++)
        {
            var file = torrentFiles[index];
            files.Add(new TorrentFileInfo(index, ToSaveRelativePath(file.Path, torrentName, torrentFiles.Count), file.Length));
        }

        return files;
    }

    /// <summary>
    /// The file's path relative to the download's save directory, matching what the engine reports
    /// post-download. A single-file torrent has no containing directory — it is saved directly as
    /// <c>&lt;torrentName&gt;</c> and MonoTorrent already reports the file's <c>Path</c> as that name — so
    /// prepending the name again would double it (<c>&lt;name&gt;/&lt;name&gt;</c>) and diverge from the
    /// engine's path, persisting a second <c>SourceFile</c> row for the same file. Multi-file torrents nest
    /// their files under the name directory, so their paths are joined.
    /// </summary>
    internal static string ToSaveRelativePath(string filePath, string torrentName, int fileCount)
    {
        var isSingleFile = fileCount == 1 && string.Equals(filePath, torrentName, StringComparison.Ordinal);
        var relative = isSingleFile ? filePath : Path.Combine(torrentName, filePath);
        return relative.Replace('\\', '/');
    }
}
