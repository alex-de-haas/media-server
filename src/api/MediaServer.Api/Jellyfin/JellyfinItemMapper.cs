using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Media;
using MediaServer.Api.Metadata;
using MediaServer.Api.Jellyfin.Streaming;

namespace MediaServer.Api.Jellyfin;

/// <summary>One loaded credit for an item: the join row plus the person it points at.</summary>
public sealed record ItemCredit(Person Person, MediaItemPerson Credit);

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
public sealed class JellyfinItemMapper(JellyfinServerContext server, MediaServerSettings settings)
{
    /// <summary>Cast credits emitted per item, by billing order. A TMDb credit block runs to hundreds.</summary>
    public const int MaxCastCredits = 30;

    /// <summary>Crew credits emitted per item, after job filtering.</summary>
    public const int MaxCrewCredits = 10;

    /// <summary>
    /// The crew jobs worth emitting, mapped to Jellyfin's person kinds. Everything else — the animators,
    /// lighting artists and stunt performers that dominate a TMDb crew list — is dropped, matching what
    /// Jellyfin's own TMDb metadata plugin stores. The original job survives as <c>Role</c>, so a client
    /// still shows "Screenplay" rather than the flattened kind.
    /// </summary>
    private static readonly Dictionary<string, string> CrewKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Director"] = "Director",
        ["Writer"] = "Writer",
        ["Screenplay"] = "Writer",
        ["Story"] = "Writer",
        ["Producer"] = "Producer",
    };

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
        ImageTags = PrimaryTags(backdropTag),
        BackdropImageTags = BackdropTags(backdropTag),
    };

    /// <summary>
    /// The synthetic top-level "Recommended" view: the part of the recommendation feed this instance
    /// actually holds, so a suggestion is something the user can press play on.
    /// </summary>
    /// <remarks>
    /// <c>CollectionType</c> is deliberately null. The shelf mixes movies and series, so <c>movies</c>
    /// would be a lie and <c>boxsets</c> a different one; a null type is Jellyfin's mixed-content
    /// library. Verified against Infuse 8.x: it renders such a view as an ordinary library, and queries
    /// it once with no type filter rather than once per type.
    /// <para>
    /// Like a catalog it owns no artwork, so it borrows some: <paramref name="backdropTag"/> is the
    /// backdrop of the title its shelf leads with. The tile moves with the shelf, which is the point —
    /// a library that suggests different films every day should not look the same every day.
    /// </para>
    /// </remarks>
    public BaseItemDto MapRecommendationsView(string? backdropTag = null) => new()
    {
        Id = JellyfinIds.RecommendationsView(),
        ServerId = server.ServerId,
        Name = "Recommended",
        Type = "CollectionFolder",
        CollectionType = null,
        IsFolder = true,
        ImageTags = PrimaryTags(backdropTag),
        BackdropImageTags = BackdropTags(backdropTag),
    };

    /// <summary>
    /// The synthetic top-level "Collections" view: a Jellyfin <c>boxsets</c> collection folder that holds the
    /// movie franchises. It owns no artwork either, so <paramref name="coverTag"/> — a representative
    /// franchise's backdrop (or poster, when it has no backdrop) — stands in for it.
    /// </summary>
    public BaseItemDto MapCollectionsView(string? coverTag = null) => new()
    {
        Id = JellyfinIds.CollectionsView(),
        ServerId = server.ServerId,
        Name = "Collections",
        Type = "CollectionFolder",
        CollectionType = "boxsets",
        IsFolder = true,
        ImageTags = PrimaryTags(coverTag),
        BackdropImageTags = BackdropTags(coverTag),
    };

    // A folder that borrows its art advertises the one tag in both slots; the image endpoint answers a
    // request for either with the same bytes.
    private static IReadOnlyDictionary<string, string>? PrimaryTags(string? tag) =>
        tag is { Length: > 0 } primary ? new Dictionary<string, string> { ["Primary"] = primary } : null;

    private static IReadOnlyList<string>? BackdropTags(string? tag) =>
        tag is { Length: > 0 } backdrop ? [backdrop] : null;

    /// <summary>
    /// Projects a movie franchise as a Jellyfin <c>BoxSet</c> folder under the Collections view. Its members
    /// are the owned movies (queried by <c>ParentId</c>); a movie still appears under its own movie catalog
    /// too, exactly as Jellyfin models collections. Artwork is the collection's own poster/backdrop, served by
    /// <c>JellyfinImageService</c> via the matching tags.
    /// <para>
    /// <c>DisplayOrder</c> is Jellyfin's per-BoxSet sort setting, and a franchise is watched in the order it
    /// was released — so it names the premiere date rather than leaving a client to fall back on the alphabet.
    /// The members come back in that order too; this only tells a client that sorts for itself which one to use.
    /// </para>
    /// </summary>
    public BaseItemDto MapBoxSet(MovieCollection collection, int childCount, string? primaryTag, string? backdropTag) => new()
    {
        Id = JellyfinIds.Collection(collection.Id),
        ServerId = server.ServerId,
        Name = collection.Name,
        SortName = collection.Name,
        Type = "BoxSet",
        CollectionType = "boxsets",
        IsFolder = true,
        ParentId = JellyfinIds.CollectionsView(),
        DateCreated = collection.UpdatedAt,
        ChildCount = childCount,
        RecursiveItemCount = childCount,
        DisplayOrder = "PremiereDate",
        ImageTags = PrimaryTags(primaryTag),
        BackdropImageTags = BackdropTags(backdropTag),
    };

    /// <summary>
    /// Projects a person as a Jellyfin <c>Person</c> item, as <c>/Persons</c> and a person-id lookup return
    /// them. A person is not a library entry, so it is <c>Virtual</c> and carries no path.
    /// </summary>
    public BaseItemDto MapPerson(Person person) => new()
    {
        Id = JellyfinPersonService.PublicId(person),
        ServerId = server.ServerId,
        Name = person.Name,
        SortName = person.Name,
        Type = "Person",
        LocationType = "Virtual",
        Overview = person.Biography,
        ImageTags = JellyfinPersonService.PrimaryTag(person) is { } tag
            ? new Dictionary<string, string> { ["Primary"] = tag }
            : null,
    };

    public BaseItemDto MapItem(
        MediaItem item,
        MetadataRecord? meta,
        IReadOnlyList<ImageAsset> images,
        IReadOnlyList<MediaSource> sources,
        UserItemDataDto userData,
        ItemParents parents,
        bool includeMediaSources,
        int? childCount = null,
        int? specialFeatureCount = null,
        IReadOnlyList<ItemCredit>? credits = null)
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
            // A Video parented to a series is an extra (creditless OP/ED, PV, …); "Clip" is the closest
            // Jellyfin ExtraType for the generic case and groups them under the client's Extras section.
            ExtraType = item.Kind == MediaKind.Video && item.SeriesId is not null ? "Clip" : null,
            SpecialFeatureCount = specialFeatureCount,
            ImageTags = PrimaryImageTags(images, item.PreferredPosterTag),
            BackdropImageTags = BackdropTags(images),
            ProviderIds = ProviderIds(item),
            People = People(credits),
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

    /// <summary>The Jellyfin type name a kind maps to, for callers filtering by <c>IncludeItemTypes</c>.</summary>
    public static string TypeNameFor(MediaKind kind) => ShapeFor(kind).Type;

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

    /// <summary>
    /// The Primary and Logo tags, ranked by <see cref="ImageSelection"/>. The same ranking runs in
    /// <see cref="JellyfinImageService"/> when the bytes are served — a client may address artwork by index
    /// rather than by the tag advertised here, so the two must agree on the order.
    /// </summary>
    private IReadOnlyDictionary<string, string>? PrimaryImageTags(IReadOnlyList<ImageAsset> images, string? pinnedPosterTag)
    {
        var tags = new Dictionary<string, string>();
        if (images.Best(ImageType.Primary, settings.PreferredLanguage, pinnedPosterTag) is { } primary)
        {
            tags["Primary"] = primary.Tag;
        }

        if (images.Best(ImageType.Logo, settings.PreferredLanguage) is { } logo)
        {
            tags["Logo"] = logo.Tag;
        }

        return tags.Count > 0 ? tags : null;
    }

    private IReadOnlyList<string>? BackdropTags(IReadOnlyList<ImageAsset> images)
    {
        var backdrops = images
            .InPreferenceOrder(ImageType.Backdrop, settings.PreferredLanguage)
            .Select(image => image.Tag)
            .ToList();
        return backdrops.Count > 0 ? backdrops : null;
    }

    /// <summary>
    /// Projects an item's credits into the client-facing people list: cast by billing order, then the crew
    /// jobs in <see cref="CrewKinds"/> with the director first. A person appears once per kind, so someone
    /// who both acted and directed is listed under each — as Jellyfin does — but two writing credits for
    /// the same person collapse to the first. Null when nothing survives, which keeps the field off items
    /// whose credits were never fetched.
    /// </summary>
    private static IReadOnlyList<BaseItemPerson>? People(IReadOnlyList<ItemCredit>? credits)
    {
        if (credits is not { Count: > 0 })
        {
            return null;
        }

        var cast = credits
            .Where(entry => entry.Credit.Role == PersonRole.Cast)
            .OrderBy(entry => entry.Credit.Order)
            .DistinctBy(entry => entry.Person.Id)
            .Take(MaxCastCredits)
            .Select(entry => MapCredit(entry, "Actor", entry.Credit.Character));

        var crew = credits
            .Where(entry => entry.Credit.Role == PersonRole.Crew)
            .Select(entry => (Entry: entry, Kind: CrewKind(entry.Credit.Job)))
            .Where(pair => pair.Kind is not null)
            .OrderBy(pair => CrewRank(pair.Kind!))
            .ThenBy(pair => pair.Entry.Credit.Order)
            .DistinctBy(pair => (pair.Entry.Person.Id, pair.Kind))
            .Take(MaxCrewCredits)
            .Select(pair => MapCredit(pair.Entry, pair.Kind!, pair.Entry.Credit.Job));

        var people = cast.Concat(crew).ToList();
        return people.Count > 0 ? people : null;
    }

    private static BaseItemPerson MapCredit(ItemCredit entry, string type, string? role) => new()
    {
        Id = JellyfinPersonService.PublicId(entry.Person),
        Name = entry.Person.Name,
        Type = type,
        Role = string.IsNullOrWhiteSpace(role) ? null : role,
        PrimaryImageTag = JellyfinPersonService.PrimaryTag(entry.Person),
    };

    private static string? CrewKind(string? job) =>
        job is { Length: > 0 } && CrewKinds.TryGetValue(job, out var kind) ? kind : null;

    // Directing before writing before producing: a client showing only the first names should show those.
    private static int CrewRank(string kind) => kind switch
    {
        "Director" => 0,
        "Writer" => 1,
        _ => 2,
    };

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
