using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
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
/// No media is produced and none is stored: the indexes were built in the background and the bytes come
/// straight out of the files as they stand. The header is computed rather than kept on disk — but it is
/// kept in memory once built, because building it reads thousands of scattered places in the film. See
/// <see cref="RemuxHeaderCache"/>. A sidecar is simply a second file
/// wrapped in a second <c>mdat</c> — an external dub is a track like any other once its samples can be
/// pointed at, which is the thing no other client of this library can do.
/// </summary>
internal sealed class RemuxStreamService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    RemuxIndexStore store,
    RemuxHeaderCache headers,
    RemuxStreamActivity activity,
    MediaServerSettings settings,
    ILogger<RemuxStreamService> logger)
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
                // The chosen dub and nothing else. Adding the file's own audio beside it was what made
                // the menu work when every track was carried; now it would only be a second sample
                // table in a header a device has to parse before the first frame.
                tracks.Add(new Mp4Synthesizer.TrackRef(1, dub.Number));
                carried.Add((sidecar.Id, new FileInfo(sidecarPath)));
            }
            else
            {
                // No dub beside the file, so the soundtrack has to come from inside it. A source that has
                // audio but none we can describe would otherwise be served as a silent film — the mirror
                // of the picture rule above, and the answer the resolver already gives for it.
                if (index.Tracks.Any(track => track.Kind == IndexedTrackKind.Audio)
                    && !index.Tracks.Any(track =>
                        track.Kind == IndexedTrackKind.Audio && RemuxCodecs.CanPackageAudio(track)))
                {
                    await DisposeAllAsync(opened);
                    return (null, RemuxRefusal.NotPackageable);
                }

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

            if (sidecar is not null)
            {
                // Subtitles live in the video file whichever soundtrack was chosen.
                foreach (var number in RemuxTrackChoice.Resolve(
                             index, null, SubtitleOrdinal(streams, subtitleStreamId)))
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

            // What the viewer actually asked for, as opposed to what is carried for the menu. An
            // external file wins when one was chosen, because it is the only choice that cannot be
            // expressed by ordering the referenced tracks.
            var subtitleDefault = externalText.Count > 0
                ? SubtitleDefault.External
                : SubtitleOrdinal(streams, subtitleStreamId) is not null
                    ? SubtitleDefault.Embedded
                    : SubtitleDefault.None;

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
                .Append('-').Append(signalling)
                // Which subtitle is *on*, not merely which are carried. The track list is identical
                // whether the first one was chosen or none was — only the enabled flag differs — so
                // without this both answers share a tag and an entry, and whichever request arrives
                // first decides whether words appear for everyone after it.
                .Append('-').Append(subtitleDefault);

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

            var key = validator.ToString();
            var etag = new EntityTagHeaderValue($"\"{key}\"");

            // The same key the tag uses, so anything that changes the body changes the entry. Built once
            // and kept: the synthesis itself is milliseconds, but it reads thousands of scattered places
            // in the film to do it, and a player asks for range after range.
            var built = headers.Get(key);
            if (built is null)
            {
                built = Mp4Synthesizer.Build(inputs, tracks, signalling, externalText, subtitleDefault);
                if (built is null)
                {
                    await DisposeAllAsync(opened);
                    return (null, RemuxRefusal.NotPackageable);
                }

                headers.Put(key, built);
                if (settings.PlaybackDiagnosticsEnabled)
                {
                    ReportShare(index, tracks, mediaSourceId);
                }
            }

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
                    new SynthesizedMp4Stream(
                        built.Header,
                        parts,
                        settings.PlaybackDiagnosticsEnabled
                            ? new RemuxStreamMeter(
                                logger, mediaSourceId.ToString("N")[..8], activity,
                                (from, to) => Whose(index, tracks, built.Header.LongLength, from, to))
                            : null),
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

    /// <summary>
    /// Which of the chosen tracks the bytes in a served range belong to, and what part of the film.
    ///
    /// The ranges alone said the player was re-reading the head of the film over and over — nine tenths
    /// of everything it fetched — but not what it thought it was fetching. A range carrying video from
    /// the opening minute is a player restarting; one carrying no chosen samples at all is a sample
    /// table pointing somewhere nothing lives. Those are different bugs, and this is the line that
    /// tells them apart.
    ///
    /// Only the first input: a sidecar's samples live in a file of their own, and a range that lands
    /// past the video says so rather than pretending.
    /// </summary>
    private static string Whose(
        MatroskaIndex index,
        IReadOnlyList<Mp4Synthesizer.TrackRef> tracks,
        long headerLength,
        long from,
        long to)
    {
        // The header sits in front of the first input, so the shift between the two is constant.
        var start = from - headerLength;
        var end = to - headerLength;
        if (end <= start || end <= 0)
        {
            return "the header";
        }

        var parts = new List<string>();
        foreach (var reference in tracks.Where(track => track.Input == 0))
        {
            if (index.Track(reference.Number) is not { } track || track.Samples.Count == 0)
            {
                continue;
            }

            var (count, bytes, first, last) = Span(track, start, end);
            if (count == 0)
            {
                continue;
            }

            // Ticks are the file's own; seconds are what a viewer and a log reader both think in.
            var seconds = index.TimestampScale / 1_000_000_000d;
            parts.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{track.Kind.ToString().ToLowerInvariant()} {count} samples {bytes / 1_000_000d:F1} MB at {first * seconds:F0}-{last * seconds:F0}s"));
        }

        return parts.Count == 0 ? "nothing we chose" : string.Join(", ", parts);
    }

    /// <summary>
    /// The chosen track's samples inside one byte range: how many, how large, and when they play.
    ///
    /// Binary search rather than a walk. A film is hundreds of thousands of samples and a player asks
    /// for a range a hundred times a second — a scan per request would make the diagnostic the slowest
    /// thing in the response it is measuring.
    /// </summary>
    private static (long Count, long Bytes, long First, long Last) Span(
        IndexedTrack track, long start, long end)
    {
        // Samples of one track run forward through the file, so the first that could overlap is found
        // rather than searched for.
        var low = 0;
        var high = track.Samples.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            var sample = track.Samples[middle];
            if (sample.Offset + sample.Size <= start)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        long count = 0, bytes = 0, first = 0, last = 0;
        for (var i = low; i < track.Samples.Count; i++)
        {
            var sample = track.Samples[i];
            if (sample.Offset >= end)
            {
                break;
            }

            if (count == 0)
            {
                first = sample.Timestamp;
            }

            last = sample.Timestamp;
            bytes += sample.Size;
            count++;
        }

        return (count, bytes, first, last);
    }

    /// <summary>
    /// How much of the file the chosen tracks actually are.
    ///
    /// The <c>mdat</c> is the source as it stands, so a player reading it sequentially fetches every
    /// track in it to play two — a source with eleven dubs is paid for in full to hear one. Whether that
    /// is worth repairing depends entirely on this ratio, which the index already knows and which no
    /// amount of reasoning about container layouts can substitute for. Logged once per header built,
    /// not once per request.
    /// </summary>
    private void ReportShare(
        MatroskaIndex index, IEnumerable<Mp4Synthesizer.TrackRef> tracks, Guid mediaSourceId)
    {
        // Input 0 only: a sidecar's samples live in a file of their own and are not part of this share.
        var chosen = tracks
            .Where(track => track.Input == 0)
            .Sum(track => index.Track(track.Number)?.Samples.Sum(sample => (long)sample.Size) ?? 0);

        if (index.SourceLength <= 0)
        {
            return;
        }

        logger.LogInformation(
            "Remux {Source}: the chosen tracks are {Chosen} MB of a {Total} MB file ({Share:P0}); "
            + "the remainder is sent and discarded.",
            mediaSourceId.ToString("N")[..8],
            chosen / 1_000_000,
            index.SourceLength / 1_000_000,
            chosen / (double)index.SourceLength);
    }

    /// <summary>An embedded subtitle's position in the container, or null when none was chosen.</summary>
    private static int? SubtitleOrdinal(IReadOnlyList<StreamRow> streams, Guid? subtitleStreamId) =>
        streams.FirstOrDefault(stream =>
            stream.Id == subtitleStreamId
            && !stream.IsExternal
            && stream.Type == StreamType.Subtitle)?.Index;

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            // Sequential, not random: a film is read from beginning to end, and telling the kernel
            // otherwise turns off the read-ahead that makes that cheap. The name is about the *pattern*,
            // not about whether seeking happens — a viewer skipping a scene still reads onwards from
            // wherever they land.
            bufferSize: 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

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
