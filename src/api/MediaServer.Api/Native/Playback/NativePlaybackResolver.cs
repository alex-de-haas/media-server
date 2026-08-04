using MediaServer.Api.Data;
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
    NativePackagingAvailability packaging)
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

        var resolutions = sources
            .Select(source => Resolve(
                source.Id, source.VersionName, source.Container, source.Path, source.Streams, appUserId, profile))
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
        NativeCapabilityProfile profile)
    {
        NativePlaybackResolution Unsupported(string reason) =>
            new(sourceId, versionName, NativePlaybackDecision.Unsupported, null, null, reason);

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

        // Dynamic range is decided before the container, because it decides *what* we would serve, not
        // merely how: a client without Dolby Vision is offered the cross-compatible signalling, and one
        // that cannot manage the source's range at all is not offered the source.
        var signalling = SignallingFor(video?.HdrFormat, profile);
        if (signalling is null)
        {
            return Unsupported(NativePlaybackReasons.UnsupportedDynamicRange);
        }

        if (Supports(profile.Containers, container))
        {
            return new NativePlaybackResolution(
                sourceId,
                versionName,
                NativePlaybackDecision.DirectPlay,
                Url: $"{NativeEndpoints.RoutePrefix}/media/{sourceId:D}?token=" +
                     tokens.Mint(appUserId, sourceId, NativeUrlTokenMethods.Read),
                Signalling: signalling,
                Reason: null);
        }

        // The codecs are fine and only the container is not, which is a packaging problem. Saying so
        // honestly beats offering a URL that will not open.
        return packaging.IsAvailable
            ? new NativePlaybackResolution(
                sourceId, versionName, NativePlaybackDecision.Remux, Url: null, Signalling: signalling, Reason: null)
            : Unsupported(NativePlaybackReasons.PackagingUnavailable);
    }

    /// <summary>
    /// The sample entry to serve, or null when this client cannot manage the source's range at all.
    /// A Dolby Vision source falls back to the cross-compatible form for a client without DV, which is
    /// correct because profile 8.1's base layer is HDR10 by definition.
    /// </summary>
    private static string? SignallingFor(string? hdrFormat, NativeCapabilityProfile profile)
    {
        if (string.IsNullOrWhiteSpace(hdrFormat) || hdrFormat.Equals("SDR", StringComparison.OrdinalIgnoreCase))
        {
            return NativeSignalling.CrossCompatible;
        }

        if (hdrFormat.Equals(DolbyVision, StringComparison.OrdinalIgnoreCase))
        {
            if (Supports(profile.HdrFormats, DolbyVision))
            {
                return NativeSignalling.DolbyVision;
            }

            // No DV on this client: serve the cross-compatible form, which it reads as HDR10 — but only
            // if it can manage HDR10 at all.
            return Supports(profile.HdrFormats, "HDR10") ? NativeSignalling.CrossCompatible : null;
        }

        return Supports(profile.HdrFormats, hdrFormat) ? NativeSignalling.CrossCompatible : null;
    }

    private static bool WithinChannels(NativeCapabilityProfile profile, StreamFacts track) =>
        profile.MaxAudioChannels is not { } max || track.Channels is not { } channels || channels <= max;

    private static bool Supports(IReadOnlyList<string>? declared, string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && declared is not null
        && declared.Any(entry => entry.Trim().Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));

    private sealed record StreamFacts(
        StreamType StreamType, string? Codec, string? HdrFormat, int? Channels, bool IsExternal);
}

/// <summary>
/// Whether this instance can repackage a file it cannot serve directly. Answered by
/// <c>remux-streaming</c> once it exists; until then the honest answer is no, and a client is told
/// so rather than handed a URL that will not open.
/// </summary>
public sealed class NativePackagingAvailability
{
    public bool IsAvailable { get; init; }
}
