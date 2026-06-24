using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Media;
using MediaServer.Api.Jellyfin.Streaming;

namespace MediaServer.Api.Jellyfin;

/// <summary>Resolved parent links for an item, already translated from internal ids to public ids.</summary>
public sealed record ItemParents(
    string? ParentId = null,
    string? SeriesId = null,
    string? SeriesName = null,
    string? SeasonId = null,
    string? SeasonName = null);

/// <summary>
/// Pure mapping from the internal media model to Jellyfin DTOs. All client-facing ids are the stable
/// public ids; raw host paths are never emitted. The library service loads the entities (localized
/// metadata, images, sources, user data, resolved parents) and this projects them.
/// </summary>
public sealed class JellyfinItemMapper(JellyfinServerContext server)
{
    /// <summary>
    /// Projects a catalog as a Jellyfin collection folder (view). Catalogs have no artwork of their own,
    /// so <paramref name="backdropTag"/> (the latest title's backdrop) is advertised as both the Primary
    /// and Backdrop image so Infuse shows a tile instead of a blank placeholder; the image endpoint serves
    /// the backdrop bytes for either request. Null when the catalog has no usable backdrop.
    /// </summary>
    public BaseItemDto MapCollectionFolder(Catalog catalog, string? backdropTag = null) => new()
    {
        Id = JellyfinIds.Catalog(catalog.Id),
        ServerId = server.ServerId,
        Name = catalog.Name,
        Type = "CollectionFolder",
        CollectionType = CollectionType(catalog.Type),
        IsFolder = true,
        DateCreated = catalog.CreatedAt,
        ImageTags = backdropTag is { Length: > 0 } tag ? new Dictionary<string, string> { ["Primary"] = tag } : null,
        BackdropImageTags = backdropTag is { Length: > 0 } backdrop ? [backdrop] : null,
    };

    public BaseItemDto MapItem(
        MediaItem item,
        MetadataRecord? meta,
        IReadOnlyList<ImageAsset> images,
        IReadOnlyList<MediaSource> sources,
        UserItemDataDto userData,
        ItemParents parents,
        bool includeMediaSources,
        int? childCount = null)
    {
        var (type, isFolder, mediaType) = ShapeFor(item.Kind);
        var name = !string.IsNullOrWhiteSpace(meta?.Title) ? meta!.Title! : item.Title;
        var year = item.Year ?? meta?.ReleaseDate?.Year;
        var container = sources.Count > 0 ? ContainerFor(sources[0]) : null;

        return new BaseItemDto
        {
            Id = item.PublicId!,
            ServerId = server.ServerId,
            Name = name,
            OriginalTitle = item.OriginalTitle,
            SortName = name,
            Etag = item.UpdatedAt.UtcTicks.ToString(),
            // Source path when sources are loaded; otherwise the item's library path so list-style
            // endpoints (which don't eager-load sources) still populate Path consistently.
            Path = sources.Count > 0 ? sources[0].Path : item.LibraryPath,
            Type = type,
            MediaType = mediaType,
            IsFolder = isFolder,
            ParentId = parents.ParentId,
            SeriesId = parents.SeriesId,
            SeriesName = parents.SeriesName,
            SeasonId = parents.SeasonId,
            SeasonName = parents.SeasonName,
            IndexNumber = item.IndexNumber,
            IndexNumberEnd = item.IndexNumberEnd,
            ParentIndexNumber = item.ParentIndexNumber,
            ProductionYear = year,
            PremiereDate = meta?.ReleaseDate,
            RunTimeTicks = RunTimeTicks(item, meta, sources),
            Overview = meta?.Overview,
            Genres = meta?.Genres is { Count: > 0 } genres ? genres : null,
            OfficialRating = meta?.OfficialRating,
            CommunityRating = meta?.CommunityRating,
            Container = container,
            DateCreated = item.AddedAt,
            ChildCount = childCount,
            RecursiveItemCount = childCount,
            ImageTags = PrimaryImageTags(images),
            BackdropImageTags = BackdropTags(images),
            ProviderIds = ProviderIds(item),
            UserData = userData with { ItemId = item.PublicId },
            MediaSources = includeMediaSources && sources.Count > 0
                ? sources.Select(source => MapMediaSource(item, source)).ToList()
                : null,
        };
    }

    public MediaSourceInfo MapMediaSource(MediaItem item, MediaSource source)
    {
        var container = ContainerFor(source);
        var streams = source.Streams
            .OrderBy(stream => stream.StreamType)
            .ThenBy(stream => stream.Index)
            .Select(MapStream)
            .ToList();

        var defaultAudio = streams.FirstOrDefault(stream => stream.Type == "Audio" && stream.IsDefault)
            ?? streams.FirstOrDefault(stream => stream.Type == "Audio");
        var defaultSubtitle = streams.FirstOrDefault(stream => stream.Type == "Subtitle" && stream.IsDefault);

        return new MediaSourceInfo
        {
            Id = JellyfinIds.MediaSource(source.Id),
            Path = source.Path,
            // The version label drives the client's version picker; single-source items fall back to the title.
            Name = string.IsNullOrWhiteSpace(source.VersionName) ? item.Title : source.VersionName,
            Container = container,
            Size = source.SizeBytes,
            RunTimeTicks = source.DurationTicks,
            Bitrate = source.Bitrate,
            ETag = source.Id.ToString("N"),
            MediaStreams = streams,
            DefaultAudioStreamIndex = defaultAudio?.Index,
            DefaultSubtitleStreamIndex = defaultSubtitle?.Index,
            DirectStreamUrl = $"/Videos/{item.PublicId}/stream.{container}?Static=true&MediaSourceId={JellyfinIds.MediaSource(source.Id)}",
        };
    }

    private static MediaStreamDto MapStream(MediaStream stream)
    {
        var type = stream.StreamType switch
        {
            StreamType.Video => "Video",
            StreamType.Audio => "Audio",
            _ => "Subtitle",
        };

        return new MediaStreamDto
        {
            Type = type,
            Index = stream.Index,
            Codec = stream.Codec,
            Language = stream.Language,
            DisplayTitle = BuildDisplayTitle(stream, type),
            IsDefault = stream.IsDefault,
            IsForced = stream.IsForced,
            IsExternal = stream.IsExternal,
            Profile = stream.Profile,
            Height = stream.Height,
            Width = stream.Width,
            AverageFrameRate = stream.FrameRate,
            RealFrameRate = stream.FrameRate,
            BitDepth = stream.BitDepth,
            VideoRange = stream.StreamType == StreamType.Video ? VideoRange(stream.HdrFormat) : null,
            VideoRangeType = stream.StreamType == StreamType.Video ? VideoRangeType(stream.HdrFormat) : "Unknown",
            AspectRatio = AspectRatio(stream),
            Channels = stream.Channels,
            SampleRate = stream.SampleRate,
            ChannelLayout = ChannelLayout(stream.Channels),
            IsTextSubtitleStream = stream.StreamType == StreamType.Subtitle && IsTextSubtitle(stream.Codec),
            SupportsExternalStream = stream.StreamType == StreamType.Subtitle,
            DeliveryMethod = stream.StreamType == StreamType.Subtitle ? (stream.IsExternal ? "External" : "Embed") : null,
        };
    }

    private static (string Type, bool IsFolder, string? MediaType) ShapeFor(MediaKind kind) => kind switch
    {
        MediaKind.Movie => ("Movie", false, "Video"),
        MediaKind.Series => ("Series", true, null),
        MediaKind.Season => ("Season", true, null),
        MediaKind.Episode => ("Episode", false, "Video"),
        _ => ("Video", false, "Video"),
    };

    private static string CollectionType(CatalogType type) => type == CatalogType.Movie ? "movies" : "tvshows";

    private static string ContainerFor(MediaSource source)
    {
        var fromPath = DirectPlay.Normalize(Path.GetExtension(source.Path));
        return string.IsNullOrEmpty(fromPath) ? DirectPlay.Normalize(source.Container) : fromPath;
    }

    private static long? RunTimeTicks(MediaItem item, MetadataRecord? meta, IReadOnlyList<MediaSource> sources)
    {
        if (sources.Count > 0 && sources[0].DurationTicks > 0)
        {
            return sources[0].DurationTicks;
        }

        return meta?.RuntimeTicks;
    }

    private static IReadOnlyDictionary<string, string>? PrimaryImageTags(IReadOnlyList<ImageAsset> images)
    {
        var tags = new Dictionary<string, string>();
        var primary = images.Where(image => image.ImageType == ImageType.Primary).OrderBy(image => image.SortOrder).FirstOrDefault();
        if (primary is not null)
        {
            tags["Primary"] = primary.Tag;
        }

        var logo = images.Where(image => image.ImageType == ImageType.Logo).OrderBy(image => image.SortOrder).FirstOrDefault();
        if (logo is not null)
        {
            tags["Logo"] = logo.Tag;
        }

        return tags.Count > 0 ? tags : null;
    }

    private static IReadOnlyList<string>? BackdropTags(IReadOnlyList<ImageAsset> images)
    {
        var backdrops = images
            .Where(image => image.ImageType == ImageType.Backdrop)
            .OrderBy(image => image.SortOrder)
            .Select(image => image.Tag)
            .ToList();
        return backdrops.Count > 0 ? backdrops : null;
    }

    private static IReadOnlyDictionary<string, string>? ProviderIds(MediaItem item)
    {
        if (item.Providers.Count == 0)
        {
            return null;
        }

        // Jellyfin keys are capitalized (e.g. "Tmdb").
        return item.Providers.ToDictionary(
            pair => string.IsNullOrEmpty(pair.Key) ? pair.Key : char.ToUpperInvariant(pair.Key[0]) + pair.Key[1..],
            pair => pair.Value);
    }

    private static string? BuildDisplayTitle(MediaStream stream, string type)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(stream.Language))
        {
            parts.Add(stream.Language);
        }

        switch (type)
        {
            case "Video" when VideoResolution.Label(stream.Width, stream.Height) is { } resolution:
                parts.Add(resolution);
                break;
            case "Audio":
                if (!string.IsNullOrEmpty(stream.Codec))
                {
                    parts.Add(stream.Codec.ToUpperInvariant());
                }

                if (ChannelLayout(stream.Channels) is { } layout)
                {
                    parts.Add(layout);
                }

                break;
        }

        if (parts.Count == 0 && !string.IsNullOrEmpty(stream.Codec))
        {
            parts.Add(stream.Codec.ToUpperInvariant());
        }

        return parts.Count > 0 ? string.Join(" - ", parts) : null;
    }

    private static bool IsSdr(string? hdrFormat) =>
        string.IsNullOrEmpty(hdrFormat) || hdrFormat.Equals("SDR", StringComparison.OrdinalIgnoreCase);

    private static string VideoRange(string? hdrFormat) => IsSdr(hdrFormat) ? "SDR" : "HDR";

    // VideoRangeType is a finer enum than VideoRange ("HDR" is not a member); collapse non-SDR to HDR10.
    private static string VideoRangeType(string? hdrFormat) => IsSdr(hdrFormat) ? "SDR" : "HDR10";

    private static string? AspectRatio(MediaStream stream)
    {
        if (stream.Width is not { } width || stream.Height is not { } height || height == 0)
        {
            return null;
        }

        var gcd = Gcd(width, height);
        return $"{width / gcd}:{height / gcd}";
    }

    private static string? ChannelLayout(int? channels) => channels switch
    {
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        8 => "7.1",
        _ => null,
    };

    private static bool IsTextSubtitle(string? codec) => codec switch
    {
        "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "vtt" or "mov_text" => true,
        _ => false,
    };

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? 1 : a;
    }
}
