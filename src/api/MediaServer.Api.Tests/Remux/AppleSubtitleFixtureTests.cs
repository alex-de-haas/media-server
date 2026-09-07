using MediaServer.Api.Remux;

namespace MediaServer.Api.Tests.Remux;

/// <summary>
/// Ties the Apple package's playable subtitle fixture to the synthesiser that produced it.
///
/// <c>RemuxSubtitlesTests</c> over in <c>src/apple</c> plays a synthesised MP4 through AVPlayer to prove a
/// <c>tx3g</c> track really hands its cues to a decoder. Swift cannot build that file — the synthesiser is
/// here — so it is committed, and a committed file drifts: the exact regressions it was written for, a
/// handler AVFoundation quietly ignores or a mis-sized style record, would leave it green while this
/// project's own tests went red.
///
/// So only the <em>source</em> is a fixture. The MP4 beside it is output, rebuilt here on every run and
/// compared byte for byte. Regenerate it — after a deliberate change to <see cref="Mp4Synthesizer"/>,
/// which also means bumping <see cref="Mp4Synthesizer.Revision"/> — with:
///
/// <code>
/// MEDIASERVER_WRITE_FIXTURES=1 dotnet test src/api/MediaServer.Api.Tests \
///     --filter FullyQualifiedName~AppleSubtitleFixture
/// </code>
/// </summary>
public sealed class AppleSubtitleFixtureTests
{
    private const string Directory_ = "src/apple/MediaKit/Tests/MediaKitTests/Fixtures";

    [Fact]
    public void The_Apple_subtitle_fixture_is_what_the_synthesiser_writes_today()
    {
        var source = File.ReadAllBytes(Fixture("remux-subtitle.mkv"));
        var at = Fixture("remux-subtitle.mp4");

        var index = MatroskaIndexer.Build(new MemoryStream(source));
        var tracks = index.Tracks
            .Where(track => track.Kind is IndexedTrackKind.Video or IndexedTrackKind.Subtitle)
            .Select(track => new Mp4Synthesizer.TrackRef(0, track.Number))
            .ToList();

        // The one embedded subtitle, turned on — which is what the Swift test then asks AVFoundation for.
        var built = Mp4Synthesizer.Build(
            [new Mp4Synthesizer.Input(index, new MemoryStream(source))],
            tracks,
            VideoSignalling.CrossCompatible,
            subtitleDefault: SubtitleDefault.Embedded);

        Assert.NotNull(built);
        Assert.Contains("tx3g", built.SampleEntries);
        Assert.Empty(built.Wrappers);

        // A single input, so the whole file is the header followed by the source it wraps.
        byte[] expected = [.. built.Header, .. source];

        if (Environment.GetEnvironmentVariable("MEDIASERVER_WRITE_FIXTURES") == "1")
        {
            File.WriteAllBytes(at, expected);
            return;
        }

        Assert.Equal(expected, File.ReadAllBytes(at));
    }

    /// <summary>
    /// The fixtures live with the tests that read them, which are in another language and another build
    /// system. Walking up to the repository root is what finds them from either.
    /// </summary>
    private static string Fixture(string name)
    {
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            var candidate = Path.Combine(at.FullName, Directory_, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{Directory_}/{name} was not found above {AppContext.BaseDirectory}");
    }
}
