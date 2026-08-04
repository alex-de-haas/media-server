using MediaServer.Api.Library;

namespace MediaServer.Api.Native;

/// <summary>
/// A title as a native client needs it: the same projection the web detail page reads, plus the one
/// thing only this surface adds — fetchable URLs.
///
/// The detail is <b>embedded, not restated</b>. Forking it would let the two surfaces drift, and a
/// client and a web page disagreeing about what a title contains is a bug rather than a platform
/// difference. See <c>docs/features/native-client-api/plan.md</c>.
/// </summary>
public sealed record NativeItemDto(LibraryDetailDto Detail, IReadOnlyList<NativeSourceUrlsDto> Sources);

/// <summary>
/// Where to fetch one edition and the sidecar files that belong to it. URLs are relative: the client
/// already knows the origin it is talking to, and an absolute URL would be wrong the moment the
/// server is reached through a different one.
/// </summary>
public sealed record NativeSourceUrlsDto(
    Guid MediaSourceId,
    string? VersionName,
    string StreamUrl,
    IReadOnlyList<NativeTrackUrlDto> Tracks);

/// <summary>A sidecar track that has a file of its own — an external dub or subtitle.</summary>
public sealed record NativeTrackUrlDto(Guid StreamId, string Type, string? Language, string? FileName, string Url);

internal static class NativeItemUrls
{
    /// <summary>
    /// Mints one token per media source and builds the URLs from it. One token covers the video and
    /// every sidecar of that source, because they are one playback: a viewer choosing the Russian dub
    /// is reading two files at once, and issuing two credentials for that would only create two things
    /// that can expire separately.
    /// </summary>
    public static List<NativeSourceUrlsDto> Build(
        LibraryDetailDto detail, int appUserId, NativeUrlTokenService tokens)
    {
        var sources = new List<NativeSourceUrlsDto>(detail.MediaSources.Count);

        foreach (var source in detail.MediaSources)
        {
            var token = tokens.Mint(appUserId, source.Id, NativeUrlTokenMethods.Read);

            var tracks = source.Streams
                // Embedded tracks live inside the container and have no file to fetch; the client
                // selects them through the player instead.
                .Where(stream => stream.IsExternal)
                .Select(stream => new NativeTrackUrlDto(
                    StreamId: stream.Id,
                    Type: stream.Type,
                    Language: stream.Language,
                    FileName: stream.FileName,
                    Url: $"{NativeEndpoints.RoutePrefix}/media/{source.Id:D}/tracks/{stream.Id:D}?token={token}"))
                .ToList();

            sources.Add(new NativeSourceUrlsDto(
                MediaSourceId: source.Id,
                VersionName: source.VersionName,
                StreamUrl: $"{NativeEndpoints.RoutePrefix}/media/{source.Id:D}?token={token}",
                Tracks: tracks));
        }

        return sources;
    }
}
