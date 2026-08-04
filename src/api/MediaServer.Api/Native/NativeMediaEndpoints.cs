using MediaServer.Api.Jellyfin.Streaming;

namespace MediaServer.Api.Native;

/// <summary>
/// Byte-range delivery for the URLs a native client hands to <c>AVPlayer</c>. These are the only
/// routes on this surface authenticated by a signed URL token rather than a bearer header, because
/// the player issues the ranged requests itself and attaches no headers of ours to them.
///
/// Every path still goes through <see cref="ICatalogPathSandbox"/>, so a source row that somehow
/// pointed outside its catalog root would resolve to nothing rather than to a file.
/// </summary>
public static class NativeMediaEndpoints
{
    public static void MapNativeMediaEndpoints(this RouteGroupBuilder group)
    {
        // The original file. AllowAnonymous is deliberate and narrow: the token in the query string
        // is the credential, it is bound to this source and to read methods, and it is refused for
        // anything else.
        group.MapMethods("/media/{mediaSourceId:guid}", ["GET", "HEAD"], async (
            Guid mediaSourceId,
            string? token,
            HttpRequest request,
            NativeUrlTokenService tokens,
            NativeMediaResolver resolver,
            CancellationToken cancellationToken) =>
        {
            if (!tokens.Validate(token, mediaSourceId, request.Method).IsValid)
            {
                return Results.NotFound();
            }

            var resolved = await resolver.ResolveSourceAsync(mediaSourceId, cancellationToken);
            return resolved is null ? Results.NotFound() : JellyfinStreamResults.File(resolved);
        }).AllowAnonymous();

        // A sidecar track: an external audio dub or subtitle living beside the video. No existing
        // client can play external audio at all, which is the point of serving it here.
        group.MapMethods("/media/{mediaSourceId:guid}/tracks/{streamId:guid}", ["GET", "HEAD"], async (
            Guid mediaSourceId,
            Guid streamId,
            string? token,
            HttpRequest request,
            NativeUrlTokenService tokens,
            NativeMediaResolver resolver,
            CancellationToken cancellationToken) =>
        {
            // The token is minted for the source, so a sidecar of that source is covered by it — one
            // playback, one credential, however many files it actually reads.
            if (!tokens.Validate(token, mediaSourceId, request.Method).IsValid)
            {
                return Results.NotFound();
            }

            var resolved = await resolver.ResolveSidecarAsync(mediaSourceId, streamId, cancellationToken);
            return resolved is null ? Results.NotFound() : JellyfinStreamResults.File(resolved);
        }).AllowAnonymous();
    }

}

/// <summary>
/// Content types for what this surface serves. It is deliberately wider than the Direct Play list the
/// Jellyfin surface gates on: that list answers "will a Jellyfin client play this container", while
/// here the client has already been told what it can open, and sidecars are not video at all.
/// </summary>
internal static class NativeContentTypes
{
    private static readonly IReadOnlyDictionary<string, string> Types =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["srt"] = "application/x-subrip",
            ["vtt"] = "text/vtt",
            ["ass"] = "text/x-ssa",
            ["ssa"] = "text/x-ssa",
            ["sup"] = "application/octet-stream",
            ["mka"] = "audio/x-matroska",
            ["ac3"] = "audio/ac3",
            ["eac3"] = "audio/eac3",
            ["dts"] = "audio/vnd.dts",
            ["aac"] = "audio/aac",
            ["flac"] = "audio/flac",
        };

    public static string For(string? extension)
    {
        var normalized = DirectPlay.Normalize(extension);
        return Types.TryGetValue(normalized, out var type) ? type : DirectPlay.ContentType(normalized);
    }
}
