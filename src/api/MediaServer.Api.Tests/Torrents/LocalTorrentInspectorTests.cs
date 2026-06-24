using MediaServer.Api.Torrents;

namespace MediaServer.Api.Tests.Torrents;

public sealed class LocalTorrentInspectorTests
{
    [Fact]
    public void ToSaveRelativePath_SingleFileTorrent_DoesNotDoubleTheName()
    {
        // A single-file torrent: MonoTorrent reports the file's Path as the torrent name, and the engine
        // saves it directly as <name>. Prepending the name would produce <name>/<name>, which diverges from
        // the engine's post-download path and persists a duplicate SourceFile row for the same file.
        var name = "Zootopia.2.rus.LostFilm.TV.avi";

        var relative = LocalTorrentInspector.ToSaveRelativePath(name, name, fileCount: 1);

        Assert.Equal(name, relative);
    }

    [Fact]
    public void ToSaveRelativePath_MultiFileTorrent_NestsUnderTheNameDirectory()
    {
        var relative = LocalTorrentInspector.ToSaveRelativePath("Season 1/ep1.mkv", "The.Show", fileCount: 2);

        Assert.Equal("The.Show/Season 1/ep1.mkv", relative);
    }

    [Fact]
    public void ToSaveRelativePath_SingleEntryMultiFileTorrent_StillNestsWhenPathDiffersFromName()
    {
        // A multi-file torrent can legitimately contain one file whose inner path differs from the torrent
        // name; that file is still saved under the name directory, so the name must be prepended.
        var relative = LocalTorrentInspector.ToSaveRelativePath("movie.mkv", "Inception.2010.1080p", fileCount: 1);

        Assert.Equal("Inception.2010.1080p/movie.mkv", relative);
    }

    [Fact]
    public void ToSaveRelativePath_NormalizesWindowsSeparators()
    {
        var relative = LocalTorrentInspector.ToSaveRelativePath("Season 1\\ep1.mkv", "The.Show", fileCount: 2);

        Assert.Equal("The.Show/Season 1/ep1.mkv", relative);
    }
}
