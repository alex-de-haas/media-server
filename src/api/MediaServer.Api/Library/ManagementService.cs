using MediaServer.Api.Configuration;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Diagnostics;
using MediaServer.Api.Native.Playback;
using MediaServer.Api.Remux;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>SQL-filtered management summaries. No media I/O or playback URLs are produced here.</summary>
public sealed class ManagementService(MediaServerDbContext db, MediaServerSettings settings, IndexingProgress? indexing = null,
    ManagementRequestDiagnostics? diagnostics = null)
{
    private Guid[] unanchoredCatalogs = [];
    private async Task ReadCatalogAvailability(CancellationToken ct)
    {
        diagnostics?.BeginStage("catalog-availability");
        if (settings.CatalogMountRoots.Count == 0)
        {
            diagnostics?.EndStage(0);
            return;
        }
        var catalogs = await db.Catalogs.AsNoTracking().ToListAsync(ct);
        unanchoredCatalogs = catalogs.Where(c => string.IsNullOrEmpty(c.MountLabel)
            ? CatalogRootResolver.ToMountRelative(settings.CatalogMountRoots, c.Root) == null
            : CatalogRootResolver.Resolve(settings.CatalogMountRoots, c.MountLabel, c.MountRelativePath) == null)
            .Select(c => c.Id).ToArray();
        diagnostics?.EndStage(catalogs.Count);
    }

    private async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> action, Func<T, int?>? count = null)
    {
        diagnostics?.BeginStage(stage);
        var result = await action();
        diagnostics?.EndStage(count?.Invoke(result));
        return result;
    }
    public const string Reference = "Apple/tvOS · HDR10 and Dolby Vision · stored media facts";
    internal static readonly NativeCapabilityProfile Profile = new(
        ["mp4", "m4v", "mov"], ["hevc", "h264"], ["aac", "ac3", "eac3"], ["SDR", "HDR10", "Dolby Vision"]);
    private static readonly string[] Views = ["all", "attention", "unavailable", "bluray", "dolby-vision", "active"];

    private sealed class SourceFacts
    {
        public Guid Id { get; init; }
        public Guid ItemId { get; init; }
        public string Container { get; init; } = "";
        public bool Disc { get; init; }
        public bool HasPath { get; init; }
        public bool Offline { get; init; }
        public long Size { get; init; }
        public string? Codec { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public string? Hdr { get; init; }
        public int? DvProfile { get; init; }
        public bool HasAudio { get; init; }
        public bool GoodAudio { get; init; }
        public bool UnknownAudio { get; init; }
        public bool RangeSupported { get; init; }
        public bool Observed { get; init; }
        public RemuxReadinessState Readiness { get; init; }
    }

    private sealed class SourceRow
    {
        public Guid Id { get; init; }
        public Guid ItemId { get; init; }
        public bool Disc { get; init; }
        public long Size { get; init; }
        public string? Codec { get; init; }
        public string Resolution { get; init; } = "unknown";
        public string? Hdr { get; init; }
        public int? DvProfile { get; init; }
        public string State { get; init; } = "unknown";
        public string? Reason { get; init; }
        public bool DvFallback { get; init; }
        public bool DvUnknown { get; init; }
    }

    private IQueryable<SourceRow> Sources()
    {
        var snapshot = indexing?.Snapshot() ?? new Dictionary<Guid, string>();
        var observed = snapshot.Keys.ToArray();
        var failed = snapshot.Where(p => p.Value == "failed").Select(p => p.Key).ToArray();
        var ready = snapshot.Where(p => p.Value == "ready").Select(p => p.Key).ToArray();
        diagnostics?.IndexSnapshot(snapshot.Count, ready.Length, failed.Length);
        var images = NativePlaybackResolver.StillImages.ToArray();
        var audio = Profile.AudioCodecs.ToArray();
        var video = Profile.VideoCodecs.ToArray();
        var containers = Profile.Containers.ToArray();
        var facts = db.MediaSources.AsNoTracking()
            .Where(s => s.MediaItem!.PublicId != null && s.MediaItem.RemovedAt == null)
            .Select(s => new
            {
                Source = s,
                Picture = s.Streams.Where(t => t.StreamType == StreamType.Video && !t.IsExternal)
                    .OrderBy(t => images.Contains((t.Codec ?? "").ToLower()) ? 1 : 0)
                    .ThenBy(t => t.Index).Select(t => new { t.Codec, t.Width, t.Height, t.HdrFormat, t.DvProfile }).FirstOrDefault(),
            })
            .Select(s => new SourceFacts
            {
                Id = s.Source.Id, ItemId = s.Source.MediaItemId, Container = s.Source.Container.ToLower(),
                Disc = s.Source.Kind == MediaSourceKind.Bluray || s.Source.Container.ToLower() == "bdmv",
                HasPath = s.Source.Path.Trim() != "", Size = s.Source.SizeBytes,
                Offline = s.Source.MediaItem!.Catalog == null || s.Source.MediaItem.Catalog.OfflineSince != null || unanchoredCatalogs.Contains(s.Source.MediaItem.Catalog.Id),
                Codec = s.Picture == null ? null : s.Picture.Codec,
                Width = s.Picture == null ? null : s.Picture.Width, Height = s.Picture == null ? null : s.Picture.Height,
                Hdr = s.Picture == null ? null : s.Picture.HdrFormat, DvProfile = s.Picture == null ? null : s.Picture.DvProfile,
                HasAudio = s.Source.Streams.Any(t => t.StreamType == StreamType.Audio),
                GoodAudio = s.Source.Streams.Any(t => t.StreamType == StreamType.Audio && t.Codec != null && audio.Contains(t.Codec.Trim().ToLower())),
                UnknownAudio = s.Source.Streams.Any(t => t.StreamType == StreamType.Audio && (t.Codec == null || t.Codec.Trim() == "")),
                RangeSupported = s.Picture == null || s.Picture.HdrFormat == null || s.Picture.HdrFormat.Trim() == ""
                    || ("·" + s.Picture.HdrFormat.Replace(" ", "").Replace(",", "·").ToUpper() + "·").Contains("·SDR·")
                    || ("·" + s.Picture.HdrFormat.Replace(" ", "").Replace(",", "·").ToUpper() + "·").Contains("·HDR10·")
                    || ("·" + s.Picture.HdrFormat.Replace(" ", "").Replace(",", "·").ToUpper() + "·").Contains("·HDR10+·")
                    || ("·" + s.Picture.HdrFormat.Replace(" ", "").Replace(",", "·").ToUpper() + "·").Contains("·HDR·")
                    || ("·" + s.Picture.HdrFormat.Replace(" ", "").Replace(",", "·").ToUpper() + "·").Contains("·DOLBYVISION·"),
                Observed = observed.Contains(s.Source.Id),
                Readiness = failed.Contains(s.Source.Id) ? RemuxReadinessState.Unsupported : ready.Contains(s.Source.Id) ? RemuxReadinessState.Ready
                    : s.Source.Container.ToLower() == "mkv" || s.Source.Container.ToLower() == "webm" || s.Source.Container.ToLower() == "mka"
                        ? RemuxReadinessState.Pending : RemuxReadinessState.Unsupported,
            });
        var judged = PlaybackEligibility.Judge(facts, f => new PlaybackFacts(f.HasPath, f.Disc,
            f.Codec == null || video.Contains(f.Codec.Trim().ToLower()), f.HasAudio, f.GoodAudio,
            f.RangeSupported, containers.Contains(f.Container),
            f.Codec == null || video.Contains(f.Codec.Trim().ToLower()), f.GoodAudio, f.Readiness));
        return judged.Select(s => new SourceRow
        {
            Id = s.Facts.Id, ItemId = s.Facts.ItemId, Disc = s.Facts.Disc, Size = s.Facts.Size,
            Codec = s.Facts.Codec, Hdr = s.Facts.Hdr, DvProfile = s.Facts.DvProfile,
            Resolution = (s.Facts.Width ?? 0) >= 3800 || (s.Facts.Height ?? 0) >= 2000 ? "2160p"
                : (s.Facts.Width ?? 0) >= 1900 || (s.Facts.Height ?? 0) >= 1000 ? "1080p"
                : (s.Facts.Width ?? 0) >= 1260 || (s.Facts.Height ?? 0) >= 700 ? "720p"
                : (s.Facts.Width ?? 0) >= 700 || (s.Facts.Height ?? 0) >= 480 ? "480p" : "unknown",
            State = s.Facts.Offline || !s.Facts.HasPath || s.Facts.Disc ? "blocked"
                : s.Facts.Codec == null || s.Facts.Codec.Trim() == "" || (!s.Facts.GoodAudio && s.Facts.UnknownAudio)
                    || (s.Facts.DvProfile == null && s.Facts.Hdr != null && s.Facts.Hdr.ToLower().Contains("dolby vision")) ? "unknown"
                : s.Reason == NativePlaybackReasons.PackagingPending ? (s.Facts.Observed ? "pending" : "unknown")
                : s.Reason == null ? "available" : "blocked",
            Reason = s.Facts.Offline ? "storage_unavailable" : !s.Facts.HasPath ? NativePlaybackReasons.NoFile
                : s.Facts.Disc ? NativePlaybackReasons.RequiresConversion
                : s.Facts.Codec == null || s.Facts.Codec.Trim() == "" || (!s.Facts.GoodAudio && s.Facts.UnknownAudio) ? "media_unknown"
                : s.Facts.DvProfile == null && s.Facts.Hdr != null && s.Facts.Hdr.ToLower().Contains("dolby vision") ? "dv_profile_unknown"
                : s.Reason == NativePlaybackReasons.PackagingPending && !s.Facts.Observed ? "preparation_unknown" : s.Reason,
            DvFallback = s.Facts.DvProfile == 7,
            DvUnknown = s.Facts.DvProfile == null && s.Facts.Hdr != null && s.Facts.Hdr.ToLower().Contains("dolby vision"),
        });
    }

    private static string[] Values(string? value) => (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static IQueryable<SourceRow> FilterSources(IQueryable<SourceRow> sources, ManagementQuery q)
    {
        var kinds = Values(q.SourceKind); var resolutions = Values(q.Resolution); var codecs = Values(q.Codec);
        var profiles = Values(q.DvProfile); var states = Values(q.State); var hdr = Values(q.Hdr);
        if (kinds.Length > 0) sources = sources.Where(s => kinds.Contains(s.Disc ? "bluray" : "file"));
        if (resolutions.Length > 0) sources = sources.Where(s => resolutions.Contains(s.Resolution));
        if (codecs.Length > 0) sources = sources.Where(s => s.Codec != null && codecs.Contains(s.Codec.ToLower()));
        if (profiles.Length > 0) sources = sources.Where(s => s.DvProfile != null && profiles.Contains(s.DvProfile.Value.ToString()));
        if (states.Length > 0) sources = sources.Where(s => states.Contains(s.State));
        if (hdr.Length > 0)
        {
            var original = sources; var matches = sources.Where(_ => false);
            foreach (var format in hdr)
            {
                var token = "·" + format.Replace(" ", "").ToUpperInvariant() + "·";
                matches = matches.Union(original.Where(s => s.Hdr != null &&
                    ("·" + s.Hdr.Replace(" ", "").Replace(",", "·").ToUpper() + "·").Contains(token)));
            }
            sources = matches;
        }
        return sources;
    }

    private static IQueryable<SourceRow> IssueSources(IQueryable<SourceRow> sources, string view) => view switch
    {
        "attention" => sources.Where(s => s.State == "blocked" || s.DvFallback),
        "bluray" => sources.Where(s => s.Disc),
        "dolby-vision" => sources.Where(s => s.DvFallback),
        "unavailable" => sources.Where(s => s.State == "blocked"),
        _ => sources,
    };

    private sealed class ItemRow
    {
        public Guid Id { get; init; }
        public MediaKind Kind { get; init; }
        public string Title { get; init; } = "";
        public int? Year { get; init; }
        public Guid? CatalogId { get; init; }
        public string CatalogName { get; init; } = "";
        public int Versions { get; init; }
        public long Size { get; init; }
        public bool SizeUnknown { get; init; }
        public int Available { get; init; }
        public int AvailableWithoutFallback { get; init; }
        public bool MatchesAttention { get; init; }
        public int Unknown { get; init; }
        public int Pending { get; init; }
        public int Issues { get; init; }
        public bool Disc { get; init; }
        public bool Dv { get; init; }
        public int Episodes { get; init; }
        public int Seasons { get; init; }
        public int Affected { get; init; }
        public int Active { get; init; }
        public int? Number { get; init; }
        public int? End { get; init; }
        public int? Season { get; init; }
    }

    private IQueryable<ItemRow> Rows(ManagementQuery q)
    {
        var sources = Sources(); var matching = FilterSources(sources, q);
        var hasSourceFilters = new[] { q.SourceKind, q.Resolution, q.Codec, q.Hdr, q.DvProfile, q.State }.Any(v => !string.IsNullOrEmpty(v));
        var kind = Enum.Parse<MediaKind>(q.Kind);
        // Fold the series-only branch out of leaf queries before SQL translation. An unconditional
        // OR EXISTS over child episodes prevents indexed source lookups for every movie/episode.
        var includeEpisodes = kind == MediaKind.Series;
        var items = db.MediaItems.AsNoTracking().Where(i => i.PublicId != null && i.RemovedAt == null && i.Kind == kind);
        if (q.ItemId is { } id) items = items.Where(i => i.Id == id);
        if (q.CatalogId is { } catalog) items = items.Where(i => i.CatalogId == catalog);
        if (q.SeriesId is { } series) items = items.Where(i => i.SeriesId == series);
        if (q.Season is { } season) items = items.Where(i => i.ParentIndexNumber == season);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + q.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            items = items.Where(i => EF.Functions.Like(i.Title, pattern, "\\") || (i.OriginalTitle != null && EF.Functions.Like(i.OriginalTitle, pattern, "\\"))
                || db.MetadataRecords.Any(m => m.MediaItemId == i.Id && m.Title != null && EF.Functions.Like(m.Title, pattern, "\\")));
        }
        var leaves = db.MediaItems.Where(i => i.PublicId != null && i.RemovedAt == null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode));
        if (hasSourceFilters) items = items.Where(i => matching.Any(s => s.ItemId == i.Id || (includeEpisodes && leaves.Any(e => e.Id == s.ItemId && e.SeriesId == i.Id))));
        var affectedEpisodes = leaves.Where(e => !hasSourceFilters || matching.Any(s => s.ItemId == e.Id));
        affectedEpisodes = q.View switch
        {
            "unavailable" => affectedEpisodes.Where(e => !sources.Any(s => s.ItemId == e.Id && s.State != "blocked")),
            "bluray" => affectedEpisodes.Where(e => matching.Any(s => s.ItemId == e.Id && s.Disc)),
            "dolby-vision" => affectedEpisodes.Where(e => matching.Any(s => s.ItemId == e.Id && s.DvFallback)),
            "active" => affectedEpisodes.Where(e => db.TranscodeJobs.Any(j => j.MediaItemId == e.Id
                    && (j.State == TranscodeJobState.Queued || j.State == TranscodeJobState.Running))
                || db.Jobs.Any(j => j.Type == LibraryMoveService.JobType && j.RelatedId != null
                    && (j.RelatedId == e.Id || j.RelatedId == e.SeriesId) && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))),
            _ => affectedEpisodes.Where(e => !sources.Any(s => s.ItemId == e.Id)
                || matching.Any(s => s.ItemId == e.Id && (s.State == "blocked" || s.DvFallback))),
        };
        var preferred = settings.PreferredLanguage ?? "";
        var rows = items.Select(i => new ItemRow
        {
            Id = i.Id, Kind = i.Kind, Year = i.Year, CatalogId = i.CatalogId, CatalogName = i.Catalog == null ? "" : i.Catalog.Name,
            Title = db.MetadataRecords.Where(m => m.MediaItemId == i.Id && m.Title != null)
                .OrderBy(m => m.Language == preferred ? 0 : 1).ThenBy(m => m.Id).Select(m => m.Title).FirstOrDefault() ?? i.Title,
            Versions = sources.Count(s => s.ItemId == i.Id || (includeEpisodes && leaves.Any(e => e.Id == s.ItemId && e.SeriesId == i.Id))),
            Size = sources.Where(s => s.ItemId == i.Id || (includeEpisodes && leaves.Any(e => e.Id == s.ItemId && e.SeriesId == i.Id))).Sum(s => s.Size),
            SizeUnknown = sources.Any(s => (s.ItemId == i.Id || (includeEpisodes && leaves.Any(e => e.Id == s.ItemId && e.SeriesId == i.Id))) && s.Size <= 0),
            Available = sources.Count(s => (s.ItemId == i.Id || (includeEpisodes && leaves.Any(e => e.Id == s.ItemId && e.SeriesId == i.Id))) && s.State == "available"),
            AvailableWithoutFallback = sources.Count(s => (s.ItemId == i.Id || (includeEpisodes && leaves.Any(e => e.Id == s.ItemId && e.SeriesId == i.Id))) && s.State == "available" && !s.DvFallback),
            MatchesAttention = matching.Any(s => (s.ItemId == i.Id || (includeEpisodes && leaves.Any(e => e.Id == s.ItemId && e.SeriesId == i.Id))) && (s.State == "blocked" || s.DvFallback))
                || (includeEpisodes && !hasSourceFilters && leaves.Any(e => e.SeriesId == i.Id && !sources.Any(s => s.ItemId == e.Id))),
            Unknown = sources.Count(s => (s.ItemId == i.Id || (includeEpisodes && leaves.Any(e => e.Id == s.ItemId && e.SeriesId == i.Id))) && s.State == "unknown"),
            Pending = sources.Count(s => (s.ItemId == i.Id || (includeEpisodes && leaves.Any(e => e.Id == s.ItemId && e.SeriesId == i.Id))) && s.State == "pending"),
            Issues = sources.Count(s => (s.ItemId == i.Id || (includeEpisodes && leaves.Any(e => e.Id == s.ItemId && e.SeriesId == i.Id))) && (s.State == "blocked" || s.DvFallback)),
            Disc = matching.Any(s => (s.ItemId == i.Id || (includeEpisodes && leaves.Any(e => e.Id == s.ItemId && e.SeriesId == i.Id))) && s.Disc),
            Dv = matching.Any(s => (s.ItemId == i.Id || (includeEpisodes && leaves.Any(e => e.Id == s.ItemId && e.SeriesId == i.Id))) && s.DvFallback),
            Episodes = includeEpisodes ? leaves.Count(e => e.SeriesId == i.Id) : 0,
            Seasons = includeEpisodes ? db.MediaItems.Count(e => e.ParentId == i.Id && e.Kind == MediaKind.Season && e.PublicId != null) : 0,
            Affected = includeEpisodes ? affectedEpisodes.Count(e => e.SeriesId == i.Id) : 0,
            Active = db.TranscodeJobs.Count(j => (j.MediaItemId == i.Id || (includeEpisodes && leaves.Any(e => e.Id == j.MediaItemId && e.SeriesId == i.Id)))
                    && (j.State == TranscodeJobState.Queued || j.State == TranscodeJobState.Running))
                + db.Jobs.Count(j => j.Type == LibraryMoveService.JobType && j.RelatedId != null && (j.RelatedId == i.Id || j.RelatedId == i.SeriesId)
                    && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running)),
            Number = i.IndexNumber, End = i.IndexNumberEnd, Season = i.ParentIndexNumber,
        });
        if (q.MinVersions is { } min) rows = rows.Where(i => i.Versions >= min);
        if (q.MaxVersions is { } max) rows = rows.Where(i => i.Versions <= max);
        return rows;
    }

    private static IQueryable<ItemRow> View(IQueryable<ItemRow> rows, string view) => view switch
    {
        "attention" => rows.Where(r => r.MatchesAttention || r.Versions == 0),
        "unavailable" => rows.Where(r => r.Available == 0 && r.Unknown == 0 && r.Pending == 0),
        "bluray" => rows.Where(r => r.Disc), "dolby-vision" => rows.Where(r => r.Dv),
        "active" => rows.Where(r => r.Active > 0), _ => rows,
    };

    public async Task<ManagementPage> ListAsync(ManagementQuery q, CancellationToken ct)
    {
        diagnostics?.BeginStage("validate");
        Validate(q);
        diagnostics?.Query(q);
        diagnostics?.EndStage();
        await ReadCatalogAvailability(ct);
        diagnostics?.BeginStage("build-query");
        var rows = Rows(q);
        diagnostics?.EndStage();
        var counts = new Dictionary<string, int>();
        foreach (var view in Views)
            counts[view] = await MeasureAsync("count." + view, () => View(rows, view).CountAsync(ct), count => count);
        var filtered = View(rows, q.View);
        var offset = q.Offset;
        // Movie deep links select the containing page of the ordinary title-ordered list, not an item filter.
        // Resolve the rank in SQL so a link into a large library does not load all titles into memory.
        if (q.FocusId is { } focusId)
        {
            var target = await MeasureAsync("focus.lookup", () => filtered.Where(r => r.Id == focusId)
                .Select(r => new { r.Id, r.Title }).SingleOrDefaultAsync(ct));
            if (target != null)
            {
                var key = target.Id.ToString().ToUpperInvariant();
                var preceding = await MeasureAsync("focus.rank", () => filtered.CountAsync(r => string.Compare(r.Title, target.Title) < 0
                    || (r.Title == target.Title && string.Compare(r.Id.ToString().ToUpper(), key) < 0), ct), count => count);
                offset = preceding / q.Limit * q.Limit;
            }
        }
        IOrderedQueryable<ItemRow> ordered = q.Sort switch
        {
            "size" => q.Descending ? filtered.OrderByDescending(r => r.Size) : filtered.OrderBy(r => r.Size),
            "versions" => q.Descending ? filtered.OrderByDescending(r => r.Versions) : filtered.OrderBy(r => r.Versions),
            "issues" when q.Kind == "Series" => q.Descending ? filtered.OrderByDescending(r => r.Affected) : filtered.OrderBy(r => r.Affected),
            "issues" => q.Descending ? filtered.OrderByDescending(r => r.Issues) : filtered.OrderBy(r => r.Issues),
            "year" => q.Descending ? filtered.OrderByDescending(r => r.Year) : filtered.OrderBy(r => r.Year),
            "episode" => q.Descending
                ? filtered.OrderByDescending(r => r.Season).ThenByDescending(r => r.Number)
                : filtered.OrderBy(r => r.Season).ThenBy(r => r.Number),
            _ => q.Descending ? filtered.OrderByDescending(r => r.Title) : filtered.OrderBy(r => r.Title),
        };
        var page = await MeasureAsync("page", () => ordered.ThenBy(r => r.Id).Skip(offset).Take(q.Limit).ToListAsync(ct), result => result.Count);
        diagnostics?.BeginStage("response");
        return new(page.Select(r => new ManagementItemDto(r.Id, r.Kind.ToString(), r.Title, r.Year, r.CatalogId, r.CatalogName,
            r.Versions, r.SizeUnknown || r.Versions == 0 ? null : r.Size, State(r), r.Issues,
            r.Affected, r.Episodes, r.Seasons, r.Active, r.Number, r.End, r.Season)).ToList(), counts[q.View], q.Limit, offset, counts, Reference);
    }

    private static string State(ItemRow r) => r.Available > 0 ? "available" : r.Unknown > 0 ? "unknown" : r.Pending > 0 ? "pending" : "blocked";

    public async Task<IReadOnlyList<ManagementSourceDto>> SourcesAsync(Guid id, ManagementQuery q, CancellationToken ct)
    {
        diagnostics?.BeginStage("validate");
        Validate(q);
        diagnostics?.Query(q);
        diagnostics?.EndStage();
        await ReadCatalogAvailability(ct);
        diagnostics?.BeginStage("build-query");
        var sources = Sources().Where(s => s.ItemId == id);
        var matches = IssueSources(FilterSources(sources, q), q.View);
        diagnostics?.EndStage();
        var result = await MeasureAsync("sources", () => sources.Select(s => new ManagementSourceDto(s.Id, s.State, s.Reason, s.DvFallback, s.DvUnknown,
            matches.Any(m => m.Id == s.Id))).ToListAsync(ct), rows => rows.Count);
        diagnostics?.BeginStage("response");
        return result;
    }

    public async Task<IReadOnlyList<LibraryAvailabilityDto>> AvailabilityAsync(Guid[] ids, CancellationToken ct)
    {
        if (ids.Length > 100) throw new ManagementValidationException("Request at most 100 titles.");
        await ReadCatalogAvailability(ct);
        var result = new List<LibraryAvailabilityDto>();
        foreach (var kind in new[] { "Movie", "Series", "Episode" })
        {
            var rows = await MeasureAsync("availability." + kind, () => Rows(new(Kind: kind)).Where(r => ids.Contains(r.Id))
                .Select(r => new { r.Id, r.Available, r.Unknown, r.Pending, r.AvailableWithoutFallback, r.Dv }).ToListAsync(ct), result => result.Count);
            result.AddRange(rows.Select(r => new LibraryAvailabilityDto(r.Id,
                r.Available > 0 ? "available" : r.Unknown > 0 ? "unknown" : r.Pending > 0 ? "pending" : "blocked",
                r.Dv && r.Available > 0 && r.AvailableWithoutFallback == 0)));
        }
        diagnostics?.BeginStage("response");
        return result;
    }

    public static void Validate(ManagementQuery q)
    {
        if (q.Kind is not ("Movie" or "Series" or "Episode") || !Views.Contains(q.View)
            || q.Sort is not ("title" or "year" or "size" or "versions" or "issues" or "episode")
            || q.Offset < 0 || q.Limit is < 1 or > 100 || q.MinVersions < 0 || q.MaxVersions < 0
            || (q.MinVersions != null && q.MaxVersions != null && q.MinVersions > q.MaxVersions)
            || q.Search?.Length > 200 || q.Season < 0
            || (q.FocusId != null && (q.Kind != "Movie" || q.Sort != "title" || q.Descending || q.ItemId != null))
            || Values(q.SourceKind).Any(v => v is not ("file" or "bluray"))
            || Values(q.Resolution).Any(v => v is not ("2160p" or "1080p" or "720p" or "480p" or "unknown"))
            || Values(q.State).Any(v => v is not ("available" or "blocked" or "pending" or "unknown"))
            || Values(q.DvProfile).Any(v => !int.TryParse(v, out var profile) || profile is < 0 or > 9)
            || new[] { q.SourceKind, q.Resolution, q.Codec, q.Hdr, q.DvProfile, q.State }.Any(v => v?.Length > 200 || Values(v).Length > 20))
            throw new ManagementValidationException("Invalid management filters or page window.");
    }
}

public sealed class ManagementValidationException(string message) : Exception(message);
