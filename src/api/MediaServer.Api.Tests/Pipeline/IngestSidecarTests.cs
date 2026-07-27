using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Probe;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// External audio tracks and subtitles (a torrent's separate <c>.mka</c>/<c>.ac3</c> dubs and <c>.srt</c>
/// files): auto-matching to the batch's videos in Identify, the review flow for unmatched tracks, and the
/// stage that places them beside their library file as sidecars.
/// <para>
/// Ingest never merges them in. That happened before this feature and was lossy — a failed mux, an absent
/// engine or a mismatched batch destroyed the track — so the pipeline now only ever moves and records them.
/// </para>
/// </summary>
public sealed class IngestSidecarTests
{
    private static readonly MetadataCandidate FmaSeries =
        new(new ProviderRef("tmdb", "31911"), "Fullmetal Alchemist Brotherhood", 2009, 1.0);

    private static readonly MetadataCandidate SomeMovie =
        new(new ProviderRef("tmdb", "603"), "Some Movie", 2020, 1.0);

    /// <summary>An audio-only file with unusable stream tags — Matroska's default "und" language and an
    /// empty title, both of which must yield to the path-inferred fallbacks.</summary>
    private static ProbeResult UntaggedAudioProbe() =>
        new("mka", TimeSpan.FromMinutes(24).Ticks, 320_000, 50_000_000,
            [new ProbedStream(StreamType.Audio, 0, "ac3", null, "und", null, null, null, null, null, 6, 48000, true, false, "")]);

    /// <summary>An audio file that states its own language and dub group, as a real <c>.mka</c> does.</summary>
    private static ProbeResult TaggedAudioProbe(string language, string title) =>
        new("mka", TimeSpan.FromMinutes(24).Ticks, 320_000, 50_000_000,
            [new ProbedStream(StreamType.Audio, 0, "ac3", null, language, null, null, null, null, null, 6, 48000, true, false, title)]);

    private static void ProbeCompanionsAs(PipelineTestHarness harness, Func<string, ProbeResult> companionProbe)
    {
        var defaultProbe = harness.MediaProbe.OnProbe;
        harness.MediaProbe.OnProbe = path =>
            path.EndsWith(".mka", StringComparison.Ordinal) || path.EndsWith(".srt", StringComparison.Ordinal)
                ? companionProbe(path)
                : defaultProbe(path);
    }

    [Fact]
    public async Task Tracks_match_their_episodes_and_land_beside_them()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = query => query.Episode is not null ? [FmaSeries] : [];
        ProbeCompanionsAs(harness, _ => UntaggedAudioProbe());

        var (ingestId, catalogId, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "FMA Brotherhood S01",
            "FMA/Fullmetal Alchemist Brotherhood S01E01.mkv",
            additionalSourceRelativePaths:
            [
                "FMA/Fullmetal Alchemist Brotherhood S01E02.mkv",
                "FMA/Rus Sound/Fullmetal Alchemist Brotherhood S01E01.mka",
                "FMA/Rus Sound/Fullmetal Alchemist Brotherhood S01E02.mka",
            ]);

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await database.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);

        // One sidecar per episode, named after the organized video and carrying the folder-inferred
        // language. No slug: each episode has only one Russian track, so the plain form is enough — and it
        // is the form clients match on.
        var externals = await database.MediaStreams.Where(stream => stream.IsExternal).ToListAsync();
        Assert.Equal(2, externals.Count);
        Assert.All(externals, stream =>
        {
            Assert.Equal(StreamType.Audio, stream.StreamType);
            Assert.Equal("rus", stream.Language);
            // "Rus Sound" is a language plus a category word, so it names a bucket rather than this track.
            Assert.Null(stream.Title);
            Assert.EndsWith(".rus.mka", stream.ExternalPath);
        });

        var catalog = await database.Catalogs.SingleAsync(item => item.Id == catalogId);
        foreach (var stream in externals)
        {
            var absolute = Path.Combine(catalog.Root, stream.ExternalPath!.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(absolute), $"expected the sidecar on disk: {stream.ExternalPath}");
        }

        // The tracks are part of the library now, not leftovers: they are Sidecar rather than Merged, and
        // each sits in its episode's folder rather than in staging.
        var files = await database.SourceFiles.ToListAsync();
        Assert.Equal(2, files.Count(file => file.AssignmentStatus == SourceFileAssignmentStatus.Sidecar));
        Assert.DoesNotContain(files, file =>
            file.AssignmentStatus == SourceFileAssignmentStatus.Sidecar && file.RelativePath.Contains(".incoming"));
    }

    [Fact]
    public async Task Several_tracks_in_one_language_are_told_apart_by_their_group()
    {
        // Three Russian dubs of one episode, the case that makes a slug necessary: language alone cannot
        // name them, and a real release distinguishes them only by the folder each sits in.
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [SomeMovie];
        ProbeCompanionsAs(harness, _ => UntaggedAudioProbe());

        var (ingestId, catalogId, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Some Movie 2020",
            "Some.Movie.2020/Some.Movie.2020.mkv",
            additionalSourceRelativePaths:
            [
                "Some.Movie.2020/RUS Sound/[AniDUB]/Some.Movie.2020.mka",
                "Some.Movie.2020/RUS Sound/[Get Smart]/Some.Movie.2020.mka",
                "Some.Movie.2020/RUS Sound/[MCA]/Some.Movie.2020.mka",
            ]);

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var externals = await database.MediaStreams.Where(stream => stream.IsExternal).ToListAsync();

        Assert.Equal(3, externals.Count);
        var names = externals.Select(stream => Path.GetFileName(stream.ExternalPath!)).OrderBy(name => name).ToList();
        Assert.All(names, name => Assert.Contains(".rus.", name));
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(names, name => name.Contains("AniDUB", StringComparison.Ordinal));
        Assert.Contains(names, name => name.Contains("Get Smart", StringComparison.Ordinal));
        Assert.Contains(names, name => name.Contains("MCA", StringComparison.Ordinal));

        var catalog = await database.Catalogs.SingleAsync(item => item.Id == catalogId);
        foreach (var stream in externals)
        {
            Assert.True(File.Exists(Path.Combine(catalog.Root, stream.ExternalPath!.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [Fact]
    public async Task A_tagged_container_names_its_own_track()
    {
        // A .mka states its language and title internally, which is why a file name never has to be the
        // source of truth — including a title no filesystem would accept verbatim.
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [SomeMovie];
        ProbeCompanionsAs(harness, _ => TaggedAudioProbe("rus", "DUB | DD5.1 @ 640 kbps"));

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Some Movie 2020",
            "Some.Movie.2020/Some.Movie.2020.mkv",
            additionalSourceRelativePaths: ["Some.Movie.2020/Some.Movie.2020.rus.mka"]);

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var external = Assert.Single(await database.MediaStreams.Where(stream => stream.IsExternal).ToListAsync());

        // The database keeps the real title; the file name only has to be legible and unique.
        Assert.Equal("DUB | DD5.1 @ 640 kbps", external.Title);
        Assert.Equal("rus", external.Language);
        Assert.DoesNotContain('|', external.ExternalPath!);
    }

    [Fact]
    public async Task The_emptied_staging_folder_is_cleared_once_the_tracks_are_out()
    {
        // Organize spares a staging root that still holds a companion — its recursive sweep would take the
        // only copy of a dub with it — so clearing what is now empty falls to the sidecar stage.
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [SomeMovie];
        ProbeCompanionsAs(harness, _ => UntaggedAudioProbe());

        var (ingestId, catalogId, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Some Movie 2020",
            "Some.Movie.2020/Some.Movie.2020.mkv",
            additionalSourceRelativePaths: ["Some.Movie.2020/RUS Sound/Some.Movie.2020.mka"]);

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var catalog = await database.Catalogs.SingleAsync(item => item.Id == catalogId);
        var incoming = Path.Combine(catalog.Root, ".incoming");

        Assert.False(
            Directory.Exists(incoming) && Directory.EnumerateFileSystemEntries(incoming).Any(),
            "the staging folder must not be left behind once its files are placed");
    }

    [Fact]
    public async Task A_dub_only_batch_keeps_its_tracks_instead_of_discarding_them()
    {
        // The case that motivated the whole feature: tracks whose videos are not in this batch used to be
        // flipped to Skipped and swept with the staging leftovers, destroying the only copy.
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [SomeMovie];
        ProbeCompanionsAs(harness, _ => UntaggedAudioProbe());

        var (ingestId, catalogId, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Some Movie 2020 RUS Sound",
            "Some.Movie.2020/RUS Sound/Some.Movie.2020.mka");

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var catalog = await database.Catalogs.SingleAsync(item => item.Id == catalogId);

        var track = await database.SourceFiles.SingleOrDefaultAsync(file => file.RelativePath.EndsWith(".mka"));
        Assert.NotNull(track);
        Assert.NotEqual(SourceFileAssignmentStatus.Skipped, track.AssignmentStatus);
        Assert.True(
            File.Exists(Path.Combine(catalog.Root, track.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
            "the track must still be on disk — nothing may destroy the only copy");
    }
}
