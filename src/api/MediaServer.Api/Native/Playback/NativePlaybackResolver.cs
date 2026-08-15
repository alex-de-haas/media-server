using MediaServer.Api.Data;
using MediaServer.Api.Probe;
using MediaServer.Api.Remux;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Native.Playback;

/// <summary>
/// Decides what a given client can actually play, per edition of a title.
///
/// This replaces the Jellyfin surface's <c>EnableDirectPlay</c>/<c>EnableDirectStream</c> flags, which
/// that surface parses and then ignores because it has only one answer to give.
/// </summary>
public sealed class NativePlaybackResolver(
    MediaServerDbContext database,
    NativeUrlTokenService tokens,
    IRemuxReadiness readiness)
{
    private const string DolbyVision = "Dolby Vision";

    public async Task<NativePlaybackResolutionResponse?> ResolveAsync(
        Guid itemId, int appUserId, NativeCapabilityProfile profile, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.AsNoTracking()
            .Where(candidate => candidate.Id == itemId && candidate.PublicId != null && candidate.RemovedAt == null)
            .Select(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (item == Guid.Empty)
        {
            return null;
        }

        var sources = await database.MediaSources.AsNoTracking()
            .Where(source => source.MediaItemId == itemId)
            .Select(source => new
            {
                source.Id,
                source.VersionName,
                source.Container,
                source.Path,
                Streams = database.MediaStreams.AsNoTracking()
                    .Where(stream => stream.MediaSourceId == source.Id)
                    // Ordered, because "the first video track" has to mean the same thing here as it
                    // does in the detail projection a client has already been shown. Unordered, SQLite
                    // may hand back a cover image first and the two surfaces disagree about what the
                    // film even is.
                    .OrderBy(stream => stream.Index)
                    .Select(stream => new StreamFacts(
                        stream.StreamType, stream.Codec, stream.HdrFormat, stream.Channels, stream.IsExternal))
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        // Asked once for all of them rather than per source: a title with six editions should not mean
        // six round trips to find out which of them have been walked.
        var ready = await readiness.ReadyAsync([.. sources.Select(source => source.Id)], cancellationToken);

        var resolutions = sources
            .Select(source => Resolve(
                source.Id, source.VersionName, source.Container, source.Path, source.Streams, appUserId,
                profile, ready.GetValueOrDefault(source.Id, RemuxReadinessState.Unsupported)))
            .ToList();

        return new NativePlaybackResolutionResponse(itemId, resolutions);
    }

    private NativePlaybackResolution Resolve(
        Guid sourceId,
        string? versionName,
        string? container,
        string? path,
        IReadOnlyList<StreamFacts> streams,
        int appUserId,
        NativeCapabilityProfile profile,
        RemuxReadinessState readiness)
    {
        NativePlaybackResolution Unsupported(string reason) =>
            new(sourceId, versionName, NativePlaybackDecision.Unsupported, null, null, null, null, reason);

        if (string.IsNullOrWhiteSpace(path))
        {
            return Unsupported(NativePlaybackReasons.NoFile);
        }

        var video = Picture(streams);
        if (video is not null && !Supports(profile.VideoCodecs, video.Codec))
        {
            // Re-encoding is out of scope for this surface, so an undecodable picture is the end of it.
            return Unsupported(NativePlaybackReasons.UnsupportedVideoCodec);
        }

        // A sidecar dub counts: the client can fetch it as its own file, so a source whose embedded
        // audio is all unplayable is still playable with one.
        var audio = streams.Where(stream => stream.StreamType == StreamType.Audio).ToList();
        if (audio.Count == 0)
        {
            return Unsupported(NativePlaybackReasons.NoAudioTrack);
        }

        if (!audio.Any(track => Supports(profile.AudioCodecs, track.Codec) && WithinChannels(profile, track)))
        {
            return Unsupported(NativePlaybackReasons.UnsupportedAudioCodec);
        }

        // Can this client manage the source's range at all? Separate question from which signalling we
        // would write, and the only one that applies to a file served as it is.
        if (!CanPresent(video?.HdrFormat, profile))
        {
            return Unsupported(NativePlaybackReasons.UnsupportedDynamicRange);
        }

        if (Supports(profile.Containers, container))
        {
            return new NativePlaybackResolution(
                sourceId,
                versionName,
                NativePlaybackDecision.DirectPlay,
                Transport: NativePlaybackTransport.ByteRange,
                Url: $"{NativeEndpoints.RoutePrefix}/media/{sourceId:D}?token=" +
                     tokens.Mint(appUserId, sourceId, NativeUrlTokenMethods.Read),
                // Deliberately null. Direct play serves the file byte for byte, so its sample entry is
                // whatever was written on disk — promising a choice here would be a promise nothing
                // keeps. The choice exists only where we build the container, which is the remux path.
                Signalling: null,
                SourceDynamicRange: video?.HdrFormat,
                Reason: null);
        }

        // The client's support is not the question from here on: we have to write the sample entries, and
        // what we can describe is a shorter list than what a client can decode. These come before the
        // readiness check because they are permanent answers — a picture or a soundtrack nothing here can
        // describe will not become describable when the walk reaches the file, and reporting "not yet"
        // about a source that will never work is the more misleading of the two.
        if (video is not null && !RemuxCodecs.CanPackageVideo(video.Codec))
        {
            // A remux with no picture is not a remux. AV1 is the case that reaches here: a recent Apple TV
            // decodes it, so nothing on the client's side of the question ruled it out.
            return Unsupported(NativePlaybackReasons.PackagingUnsupportedVideo);
        }

        // And one whose only audio track we cannot describe would play as a silent film.
        if (!streams.Any(stream =>
                stream.StreamType == StreamType.Audio && RemuxCodecs.CanPackageAudio(stream.Codec)))
        {
            return Unsupported(NativePlaybackReasons.PackagingUnsupportedAudio);
        }

        // The codecs are fine and only the container is not, which is a packaging problem. Saying so
        // honestly beats offering a URL that will not open.
        if (readiness != RemuxReadinessState.Ready)
        {
            // Two different answers, and the difference matters to a client: a container nothing can
            // index will never become playable, while a file the walk has not reached yet will.
            return Unsupported(readiness == RemuxReadinessState.Pending
                ? NativePlaybackReasons.PackagingPending
                : NativePlaybackReasons.PackagingUnavailable);
        }

        var signalling = SignallingFor(video?.HdrFormat, profile);
        return new NativePlaybackResolution(
            sourceId,
            versionName,
            NativePlaybackDecision.Remux,
            Transport: NativePlaybackTransport.ByteRange,
            Url: $"{NativeEndpoints.RoutePrefix}/media/{sourceId:D}/remux?token=" +
                 tokens.Mint(appUserId, sourceId, NativeUrlTokenMethods.Read) +
                 $"&signalling={signalling}",
            // Here the signalling is ours to choose, because we are the ones writing the container.
            Signalling: signalling,
            SourceDynamicRange: video?.HdrFormat,
            Reason: null);
    }

    /// <summary>
    /// Whether this client can present the source's dynamic range in some form.
    /// </summary>
    /// <remarks>
    /// Not an equality check, because the two sides name things differently: a probe says what it can
    /// see and a client says what it decodes. Everything in this vocabulary rests on HDR10 — Dolby
    /// Vision's base layer is HDR10 by definition, HDR10+ degrades to its, and a plain <c>HDR</c> is what
    /// the header probe reports when a container header will not say which of the two it is. So a client
    /// declaring HDR10 can present them all; what changes is the signalling, not whether it can be
    /// shown.
    /// </remarks>
    private static bool CanPresent(string? hdrFormat, NativeCapabilityProfile profile)
    {
        if (IsSdr(hdrFormat))
        {
            return true;
        }

        // The field holds what a probe wrote, and this library contains values naming more than one
        // format — "Dolby Vision · HDR10", which is what a profile 8.1 file honestly is. A source is
        // presentable when *any* of the formats it names can be shown.
        return Formats(hdrFormat!).Any(format =>
            Supports(profile.HdrFormats, format)
            // Everything in this vocabulary rests on HDR10, so a client declaring it can present them
            // all. Dolby Vision carries a base layer; HDR10+ degrades to its; and a plain "HDR" is what
            // the header probe reports when a container header will not say which of the two it is — a
            // word no client ever claims, since clients name the formats they decode.
            || (DegradesToHdr10(format) && Supports(profile.HdrFormats, Hdr10)));
    }

    /// <summary>
    /// The formats a stored value names. Usually one, and more when a probe recorded a file as several —
    /// splitting is what keeps a compound from being compared whole against a vocabulary of singles.
    /// </summary>
    private static IEnumerable<string> Formats(string hdrFormat) =>
        // The middle dot this library's data uses, and a comma as the obvious alternative. Not '+',
        // which is part of "HDR10+" rather than a separator between two names.
        hdrFormat.Split(['\u00b7', ','], StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Prepend(hdrFormat.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same question, reachable by a test.</summary>
    internal static bool CanPresentFor(string? hdrFormat, NativeCapabilityProfile profile) =>
        CanPresent(hdrFormat, profile);

    private const string Hdr10 = "HDR10";

    private static bool DegradesToHdr10(string hdrFormat) =>
        hdrFormat.Equals(DolbyVision, StringComparison.OrdinalIgnoreCase)
        || hdrFormat.Equals("HDR10+", StringComparison.OrdinalIgnoreCase)
        || hdrFormat.Equals(ProbeVocabulary.Hdr, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which sample entry to write when <b>we</b> produce the container. Only meaningful on the remux
    /// path: a file served as it is carries whatever signalling it was written with, which nothing here
    /// gets to choose.
    /// </summary>
    private static string? SignallingFor(string? hdrFormat, NativeCapabilityProfile profile)
    {
        if (IsSdr(hdrFormat))
        {
            return NativeSignalling.CrossCompatible;
        }

        return hdrFormat!.Equals(DolbyVision, StringComparison.OrdinalIgnoreCase)
               && Supports(profile.HdrFormats, DolbyVision)
            ? NativeSignalling.DolbyVision
            : NativeSignalling.CrossCompatible;
    }

    private static bool IsSdr(string? hdrFormat) =>
        string.IsNullOrWhiteSpace(hdrFormat) || hdrFormat.Equals("SDR", StringComparison.OrdinalIgnoreCase);

    private static bool WithinChannels(NativeCapabilityProfile profile, StreamFacts track) =>
        profile.MaxAudioChannels is not { } max || track.Channels is not { } channels || channels <= max;

    // The profile is request input, so a null or blank entry is something a client can actually send.
    // Treated as a non-match rather than dereferenced: a malformed body must not become a 500.
    /// <summary>
    /// Codecs that are a still image rather than a film. A cover the muxer did not flag as attached art
    /// is an ordinary video stream in every way the database can see, and this library holds such files:
    /// the remux path already skips them, and a resolver that judged one would refuse a perfectly
    /// playable title for having an undecodable "picture".
    /// </summary>
    private static readonly HashSet<string> StillImages =
        new(StringComparer.OrdinalIgnoreCase) { "mjpeg", "png", "bmp", "gif", "webp" };

    /// <summary>
    /// The stream that is actually the film: the first video track that is not a still image, and only
    /// then the first video track at all — a file whose one video stream is a cover is broken either
    /// way, and refusing it with a reason beats pretending it has no picture.
    /// </summary>
    internal static StreamFacts? Picture(IReadOnlyList<StreamFacts> streams)
    {
        var video = streams.Where(stream => stream.StreamType == StreamType.Video).ToList();
        return video.FirstOrDefault(stream => !StillImages.Contains(stream.Codec ?? string.Empty))
            ?? video.FirstOrDefault();
    }

    private static bool Supports(IReadOnlyList<string>? declared, string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && declared is not null
        && declared.Any(entry =>
               !string.IsNullOrWhiteSpace(entry)
               && entry.Trim().Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The same choice, over the entity rather than the projection, so a test can make one.</summary>
    internal static StreamFacts? PictureFor(IEnumerable<MediaStream> streams) =>
        Picture([.. streams.OrderBy(stream => stream.Index).Select(stream => new StreamFacts(
            stream.StreamType, stream.Codec, stream.HdrFormat, stream.Channels, stream.IsExternal))]);

    internal sealed record StreamFacts(
        StreamType StreamType, string? Codec, string? HdrFormat, int? Channels, bool IsExternal);
}

/// <summary>
/// Whether this instance can repackage a file it cannot serve directly. Answered by
/// <c>remux-streaming</c> once it exists; until then the honest answer is no, and a client is told
/// so rather than handed a URL that will not open.
/// </summary>

