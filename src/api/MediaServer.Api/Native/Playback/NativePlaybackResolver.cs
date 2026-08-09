using MediaServer.Api.Data;
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

        var video = streams.FirstOrDefault(stream => stream.StreamType == StreamType.Video);
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
    /// Whether this client can present the source's dynamic range in some form. A Dolby Vision source
    /// is presentable to a client with only HDR10, because profile 8.1's base layer is HDR10 by
    /// definition — what changes is the signalling, not whether it can be shown.
    /// </summary>
    private static bool CanPresent(string? hdrFormat, NativeCapabilityProfile profile)
    {
        if (IsSdr(hdrFormat))
        {
            return true;
        }

        return hdrFormat!.Equals(DolbyVision, StringComparison.OrdinalIgnoreCase)
            ? Supports(profile.HdrFormats, DolbyVision) || Supports(profile.HdrFormats, "HDR10")
            : Supports(profile.HdrFormats, hdrFormat);
    }

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
    private static bool Supports(IReadOnlyList<string>? declared, string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && declared is not null
        && declared.Any(entry =>
               !string.IsNullOrWhiteSpace(entry)
               && entry.Trim().Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));

    private sealed record StreamFacts(
        StreamType StreamType, string? Codec, string? HdrFormat, int? Channels, bool IsExternal);
}

/// <summary>
/// Whether this instance can repackage a file it cannot serve directly. Answered by
/// <c>remux-streaming</c> once it exists; until then the honest answer is no, and a client is told
/// so rather than handed a URL that will not open.
/// </summary>

