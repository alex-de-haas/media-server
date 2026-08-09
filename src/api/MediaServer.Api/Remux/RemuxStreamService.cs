using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace MediaServer.Api.Remux;

/// <summary>What a client is handed when it asks for a repackaged source.</summary>
internal sealed record RemuxStream(
    Stream Content, string ContentType, EntityTagHeaderValue ETag, DateTimeOffset LastModified);

/// <summary>Why a source cannot be served repackaged, in terms a client can act on.</summary>
internal enum RemuxRefusal
{
    /// <summary>No such source, or one this viewer must not see.</summary>
    Unknown,

    /// <summary>The index has not been built yet. It is being built; coming back later works.</summary>
    NotIndexed,

    /// <summary>Indexed, but nothing in it can be described as an MP4 track.</summary>
    NotPackageable,
}

/// <summary>
/// Serves a media source as an MP4 computed over the untouched file, optionally with a sidecar dub
/// alongside it.
///
/// Nothing is produced here and nothing is stored: the indexes were built in the background, the header is
/// computed per request, and the media comes straight out of the files. A sidecar is simply a second file
/// wrapped in a second <c>mdat</c> — an external dub is a track like any other once its samples can be
/// pointed at, which is the thing no other client of this library can do.
/// </summary>
public sealed class RemuxStreamService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    RemuxIndexStore store)
{
    private sealed record StreamRow(
        Guid Id, StreamType Type, int Index, bool IsExternal, string? ExternalPath, string? Codec,
        string? Language);

    internal async Task<(RemuxStream? Stream, RemuxRefusal Refusal)> OpenAsync(
        Guid mediaSourceId,
        Guid? audioStreamId,
        Guid? subtitleStreamId,
        VideoSignalling signalling,
        CancellationToken cancellationToken)
    {
        var (absolute, catalog) = await ResolveAsync(mediaSourceId, cancellationToken);
        if (absolute is null || catalog is null)
        {
            return (null, RemuxRefusal.Unknown);
        }

        var index = store.Load(mediaSourceId, absolute);
        if (index is null)
        {
            return (null, RemuxRefusal.NotIndexed);
        }

        // A source with a picture we cannot describe would otherwise be served as an audio-only container.
        // The resolver gates on what the *client* can decode and this gates on what we can *write*, which
        // are not the same question — the same drift that once produced a video with no sound, on the other
        // axis. AV1 is the live case: a recent Apple TV decodes it and nothing here can write its entry.
        if (index.Tracks.Any(track => track.Kind == IndexedTrackKind.Video)
            && RemuxTrackChoice.Video(index) is null)
        {
            return (null, RemuxRefusal.NotPackageable);
        }

        var streams = await database.MediaStreams.AsNoTracking()
            .Where(stream => stream.MediaSourceId == mediaSourceId)
            .Select(stream => new StreamRow(
                stream.Id, stream.StreamType, stream.Index, stream.IsExternal, stream.ExternalPath,
                stream.Codec, stream.Language))
            .ToListAsync(cancellationToken);

        // A chosen dub that lives in its own file is the reason the layout takes more than one input.
        var sidecar = streams.FirstOrDefault(stream =>
            stream.Id == audioStreamId && stream.IsExternal && stream.Type == StreamType.Audio);

        var opened = new List<Stream>();
        // Every file the answer is made of, so a cache validator can cover all of them and not just the
        // video: a dub replaced by one of the same length, or a subtitle file edited in place, changes
        // what is served without changing anything about the source.
        var carried = new List<(Guid Id, FileInfo File)>();

        try
        {
            var inputs = new List<Mp4Synthesizer.Input>();
            var tracks = new List<Mp4Synthesizer.TrackRef>();

            var source = Open(absolute);
            opened.Add(source);
            inputs.Add(new Mp4Synthesizer.Input(index, source));

            if (RemuxTrackChoice.Video(index) is { } video)
            {
                tracks.Add(new Mp4Synthesizer.TrackRef(0, video.Number));
            }

            if (sidecar is not null)
            {
                // The sidecar's own index is keyed by its stream row, and is built by the same worker.
                // Without one there is nothing to point at, so this is "not yet" rather than "never".
                if (!sandbox.TryResolve(catalog, sidecar.ExternalPath!, out var sidecarPath)
                    || !File.Exists(sidecarPath))
                {
                    await DisposeAllAsync(opened);
                    return (null, RemuxRefusal.Unknown);
                }

                var sidecarIndex = store.Load(sidecar.Id, sidecarPath);
                if (sidecarIndex is null)
                {
                    await DisposeAllAsync(opened);
                    return (null, RemuxRefusal.NotIndexed);
                }

                var dub = sidecarIndex.Tracks.FirstOrDefault(track =>
                    track.Kind == IndexedTrackKind.Audio && RemuxCodecs.CanPackageAudio(track));
                if (dub is null)
                {
                    await DisposeAllAsync(opened);
                    return (null, RemuxRefusal.NotPackageable);
                }

                var sidecarStream = Open(sidecarPath);
                opened.Add(sidecarStream);
                inputs.Add(new Mp4Synthesizer.Input(sidecarIndex, sidecarStream));
                tracks.Add(new Mp4Synthesizer.TrackRef(1, dub.Number));
                carried.Add((sidecar.Id, new FileInfo(sidecarPath)));
            }
            else
            {
                var embedded = streams.FirstOrDefault(stream => stream.Id == audioStreamId && !stream.IsExternal);
                foreach (var number in RemuxTrackChoice.Resolve(
                             index, embedded?.Index, SubtitleOrdinal(streams, subtitleStreamId)))
                {
                    if (index.Track(number) is { Kind: not IndexedTrackKind.Video })
                    {
                        tracks.Add(new Mp4Synthesizer.TrackRef(0, number));
                    }
                }
            }

            if (sidecar is not null && SubtitleOrdinal(streams, subtitleStreamId) is { } subtitleOrdinal)
            {
                foreach (var number in RemuxTrackChoice.Resolve(index, null, subtitleOrdinal))
                {
                    if (index.Track(number) is { Kind: IndexedTrackKind.Subtitle })
                    {
                        tracks.Add(new Mp4Synthesizer.TrackRef(0, number));
                    }
                }
            }

            // A subtitle beside the video has no index and needs none: a film's dialogue is a hundred
            // kilobytes, so it is read here rather than walked in the background.
            var externalText = new List<(IReadOnlyList<TextCue>, string?)>();
            var textSidecar = streams.FirstOrDefault(stream =>
                stream.Id == subtitleStreamId && stream.IsExternal && stream.Type == StreamType.Subtitle);
            if (textSidecar is not null
                && sandbox.TryResolve(catalog, textSidecar.ExternalPath!, out var textPath)
                && SubtitleFile.IsConvertible(textPath))
            {
                var cues = SubtitleFile.Read(textPath);
                if (cues.Count > 0)
                {
                    externalText.Add((cues, textSidecar.Language));
                    carried.Add((textSidecar.Id, new FileInfo(textPath)));
                }
            }

            var built = Mp4Synthesizer.Build(inputs, tracks, signalling, externalText);
            if (built is null)
            {
                await DisposeAllAsync(opened);
                return (null, RemuxRefusal.NotPackageable);
            }

            var file = new FileInfo(absolute);
            // The tag covers everything the answer is made of: the source, the tracks chosen, the
            // signalling asked for, and every sidecar carried with it. A viewer switching dub gets a
            // different body, and so does one whose subtitle file was edited — both must get a different
            // tag, or a conditional request is answered 304 with the old audio or the old words.
            var validator = new System.Text.StringBuilder()
                .Append(mediaSourceId.ToString("N"))
                .Append('-').Append(file.Length.ToString("x"))
                .Append('-').Append(file.LastWriteTimeUtc.Ticks.ToString("x"))
                .Append('-').Append(string.Join('.', tracks.Select(track => $"{track.Input}:{track.Number}")))
                .Append('-').Append(signalling);

            var lastModified = file.LastWriteTimeUtc;
            foreach (var (id, info) in carried)
            {
                validator.Append('-').Append(id.ToString("N"))
                    .Append(':').Append(info.Length.ToString("x"))
                    .Append(':').Append(info.LastWriteTimeUtc.Ticks.ToString("x"));

                // The freshest of everything served, not merely of the video.
                if (info.LastWriteTimeUtc > lastModified)
                {
                    lastModified = info.LastWriteTimeUtc;
                }
            }

            var etag = new EntityTagHeaderValue($"\"{validator}\"");

            // The wrapper of every input after the first sits between the files, which is where the
            // sample offsets expect it. Nothing else knows to put it there.
            var parts = new List<Stream> { opened[0] };
            for (var i = 1; i < opened.Count; i++)
            {
                parts.Add(new MemoryStream(built.Wrappers[i - 1]));
                parts.Add(opened[i]);
            }

            return (
                new RemuxStream(
                    new SynthesizedMp4Stream(built.Header, parts),
                    "video/mp4",
                    etag,
                    lastModified),
                RemuxRefusal.Unknown);
        }
        catch
        {
            await DisposeAllAsync(opened);
            throw;
        }
    }

    /// <summary>An embedded subtitle's position in the container, or null when none was chosen.</summary>
    private static int? SubtitleOrdinal(IReadOnlyList<StreamRow> streams, Guid? subtitleStreamId) =>
        streams.FirstOrDefault(stream =>
            stream.Id == subtitleStreamId
            && !stream.IsExternal
            && stream.Type == StreamType.Subtitle)?.Index;

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);

    private static async Task DisposeAllAsync(IEnumerable<Stream> streams)
    {
        foreach (var stream in streams)
        {
            await stream.DisposeAsync();
        }
    }

    /// <summary>
    /// The file behind a source and the catalog that confines it, subject to the same rules as every other
    /// path on this surface: unpublished and tombstoned items resolve to nothing.
    /// </summary>
    private async Task<(string? Absolute, Catalog? Catalog)> ResolveAsync(
        Guid mediaSourceId, CancellationToken cancellationToken)
    {
        var row = await database.MediaSources.AsNoTracking()
            .Where(source => source.Id == mediaSourceId)
            .Join(database.MediaItems.AsNoTracking(), source => source.MediaItemId, item => item.Id,
                (source, item) => new { source.Path, item.CatalogId, item.PublicId, item.RemovedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null || row.PublicId is null || row.RemovedAt is not null || row.CatalogId is null)
        {
            return (null, null);
        }

        var catalog = await database.Catalogs.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == row.CatalogId, cancellationToken);

        return catalog is not null && sandbox.TryResolve(catalog, row.Path, out var absolute)
            && File.Exists(absolute)
            ? (absolute, catalog)
            : (null, null);
    }
}
