using System.Text.Json;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Groups;

/// <summary>Shared web/native group reads and administrator-only definition changes.</summary>
public sealed class GroupService(MediaServerDbContext database, LibraryReadService library, MediaServerSettings settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int CountBatchSize = 32;
    private const string LikeEscape = "\\";
    public static readonly string[] Resolutions = ["480p", "720p", "1080p", "2160p"];
    public static readonly string[] HdrFormats = ["HDR10", "HDR10+", "Dolby Vision", "HLG", "HDR", "any"];

    /// <summary>Lists even empty groups; counts use the current matching titles without loading poster data.</summary>
    public async Task<IReadOnlyList<GroupSummaryDto>> ListAsync(CancellationToken ct)
    {
        var groups = await database.Set<MediaGroup>().AsNoTracking().OrderBy(g => g.Name).ThenBy(g => g.Id).ToListAsync(ct);
        var result = new List<GroupSummaryDto>();
        // Bound compound-query size while evaluating every group's current rules in SQL.
        // Scalar counts retain empty groups and avoid transferring matching title rows.
        foreach (var batch in groups.Chunk(CountBatchSize))
        {
            IQueryable<GroupCount>? counts = null;
            foreach (var group in batch)
            {
                var members = Members(group);
                var count = database.Set<MediaGroup>().Where(g => g.Id == group.Id)
                    .Select(g => new GroupCount { Id = g.Id, Total = members.Count() });
                counts = counts is null ? count : counts.Concat(count);
            }
            var summaries = await counts!.ToDictionaryAsync(g => g.Id, ct);
            result.AddRange(batch.Where(g => summaries.ContainsKey(g.Id))
                .Select(g => new GroupSummaryDto(g.Id, g.Name, g.Kind, g.CatalogType, summaries[g.Id].Total)));
        }
        return result;
    }

    private sealed class GroupCount
    {
        public Guid Id { get; init; }
        public int Total { get; init; }
    }

    /// <summary>Reads one group page, with no minimum membership threshold.</summary>
    public async Task<GroupDetailDto?> GetAsync(Guid id, int? userId, int limit, int offset, CancellationToken ct)
    {
        var group = await database.Set<MediaGroup>().AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return null;
        var page = await PageAsync(Members(group), userId, limit, offset, ct);
        return new(id, group.Name, group.Kind, group.CatalogType, page.Items, page.Total, page.Limit, page.Offset);
    }

    /// <summary>Returns the full stored definition for editing without losing hidden manual links.</summary>
    public async Task<GroupDefinitionDto?> DefinitionAsync(Guid id, CancellationToken ct)
    {
        var group = await database.Set<MediaGroup>().AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, ct);
        return group is null ? null : new(id, group.Name, group.Kind, group.CatalogType, ReadRules(group),
            await database.Set<MediaGroupMember>().Where(m => m.MediaGroupId == id).Select(m => m.MediaItemId).ToListAsync(ct));
    }

    /// <summary>Creates or replaces a definition and its membership atomically.</summary>
    public async Task<GroupDefinitionDto?> SaveAsync(Guid? id, SaveGroupRequest request, CancellationToken ct)
    {
        Validate(request);
        var group = id is { } existing
            ? await database.Set<MediaGroup>().SingleOrDefaultAsync(g => g.Id == existing, ct)
            : new MediaGroup { Id = Guid.NewGuid(), Name = "", Kind = request.Kind, CatalogType = request.CatalogType, RulesJson = "" };
        if (group is null) return null;
        if (group.Kind != request.Kind || group.CatalogType != request.CatalogType) throw new GroupValidationException("A group's mode and catalog type cannot be changed.");
        var ids = request.MemberIds.Distinct().ToArray();
        var members = await database.Set<MediaGroupMember>().Where(m => m.MediaGroupId == group.Id).ToListAsync(ct);
        var previous = members.Select(m => m.MediaItemId).ToHashSet();
        var addedIds = ids.Where(itemId => !previous.Contains(itemId)).ToArray();
        // Existing links survive tombstones and cross-type moves; only new selections must match
        // the current catalog type. This also permits renaming a group after a member moved away.
        var type = Enum.Parse<CatalogType>(request.CatalogType, true);
        var validIds = await database.MediaItems.Where(i => addedIds.Contains(i.Id) && i.Kind == (type == CatalogType.Movie ? MediaKind.Movie : MediaKind.Series) && i.ParentId == null
            && database.Catalogs.Any(c => c.Id == i.CatalogId && c.Type == type))
            .Select(i => i.Id).ToListAsync(ct);
        if (validIds.Count != addedIds.Length) throw new GroupValidationException("Choose existing titles from the selected catalog type.");
        group.Name = request.Name.Trim();
        group.RulesJson = JsonSerializer.Serialize(request.Rules, Json);
        if (id is null) database.Add(group);
        database.RemoveRange(members.Where(m => !ids.Contains(m.MediaItemId)));
        database.AddRange(ids.Where(movie => !previous.Contains(movie)).Select(movie => new MediaGroupMember
            { MediaGroupId = group.Id, MediaItemId = movie }));
        await database.SaveChangesAsync(ct);
        return new(group.Id, group.Name, group.Kind, group.CatalogType, request.Rules, ids);
    }

    /// <summary>Deletes the definition and links only; never removes titles or files.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        return await database.Set<MediaGroup>().Where(g => g.Id == id).ExecuteDeleteAsync(ct) != 0;
    }

    /// <summary>Previews exactly the same query used for saved membership.</summary>
    public async Task<LibrarySearchPage> PreviewAsync(SaveGroupRequest request, int? userId, int limit, int offset, CancellationToken ct)
    {
        Validate(request);
        return await PageAsync(request.Kind == "smart" ? Smart(request.Rules, request.CatalogType) : Items(request.CatalogType).Where(i => request.MemberIds.Contains(i.Id)),
            userId, limit, offset, ct);
    }

    /// <summary>Searches a bounded title page for the manual membership picker.</summary>
    public Task<LibrarySearchPage> CandidatesAsync(string catalogType, string? title, int? userId, int limit, int offset, CancellationToken ct)
    {
        var items = Items(catalogType);
        if (!string.IsNullOrWhiteSpace(title))
        {
            var pattern = SearchPattern(title);
            items = items.Where(i => EF.Functions.Like(i.Title, pattern, LikeEscape) || database.MetadataRecords.Any(r =>
                r.MediaItemId == i.Id && r.Title != null && EF.Functions.Like(r.Title, pattern, LikeEscape)));
        }
        return PageAsync(items, userId, limit, offset, ct);
    }

    /// <summary>Lists bounded, searchable keyword/genre choices without loading the title library.</summary>
    public async Task<GroupOptionsDto> OptionsAsync(string catalogType, string? search, CancellationToken ct)
    {
        var items = Items(catalogType);
        var tags = database.MetadataTags.AsNoTracking().Where(t => t.Value != "" && database.MetadataRecords.Any(r =>
            r.Id == t.MetadataRecordId && items.Any(i => i.Id == r.MediaItemId)));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = SearchPattern(search);
            tags = tags.Where(t => EF.Functions.Like(t.Value, pattern, LikeEscape));
        }
        return new(Resolutions, HdrFormats,
            await tags.Where(t => t.Kind == MetadataTagKind.Keyword).Select(t => t.Value).Distinct().Order().Take(100).ToListAsync(ct),
            await tags.Where(t => t.Kind == MetadataTagKind.Genre).Select(t => t.Value).Distinct().Order().Take(100).ToListAsync(ct));
    }

    private static string SearchPattern(string term) => "%" + term.Trim()
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private IQueryable<MediaItem> Items(string catalogType)
    {
        if (catalogType is not ("movie" or "series" or "anime")) throw new GroupValidationException("Choose a valid catalog type.");
        var type = Enum.Parse<CatalogType>(catalogType, true);
        return database.MediaItems.AsNoTracking().Where(i => i.Kind == (type == CatalogType.Movie ? MediaKind.Movie : MediaKind.Series)
            && i.ParentId == null && i.PublicId != null && i.RemovedAt == null
            && database.Catalogs.Any(c => c.Id == i.CatalogId && c.Type == type));
    }

    private IQueryable<MediaItem> Members(MediaGroup group) => group.Kind == "smart" ? Smart(ReadRules(group), group.CatalogType)
        : Items(group.CatalogType).Where(i => database.Set<MediaGroupMember>().Any(m => m.MediaGroupId == group.Id && m.MediaItemId == i.Id));

    private static GroupRules ReadRules(MediaGroup group) => JsonSerializer.Deserialize<GroupRules>(group.RulesJson, Json)!;

    private IOrderedQueryable<MediaItem> Ordered(IQueryable<MediaItem> query)
    {
        var preferred = settings.PreferredLanguage.ToLowerInvariant();
        var primary = preferred.Split('-')[0];
        return query.OrderBy(item => database.MetadataRecords.Where(r => r.MediaItemId == item.Id)
            .OrderBy(r => r.Language.ToLower() == preferred ? 0 : r.Language.ToLower() == primary || r.Language.ToLower().StartsWith(primary + "-") ? 1 : 2)
            .ThenBy(r => r.Id).Select(r => r.Title).FirstOrDefault() ?? item.Title).ThenBy(item => item.Id);
    }

    private async Task<LibrarySearchPage> PageAsync(IQueryable<MediaItem> query, int? userId, int limit, int offset, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        var total = await query.CountAsync(ct);
        var movies = await Ordered(query).Skip(offset).Take(limit).ToListAsync(ct);
        return new(await library.ProjectCardsAsync(movies, userId, ct), total, limit, offset);
    }

    private IQueryable<MediaItem> Smart(GroupRules rules, string catalogType)
    {
        var all = rules.Match == "all";
        var result = all ? Items(catalogType) : Items(catalogType).Where(_ => false);
        var sources = rules.Conditions.Where(c => c.Field is "resolution" or "hdr").ToArray();
        if (sources.Length != 0)
        {
            var video = database.MediaStreams.Where(s => s.StreamType == StreamType.Video
                && s.MediaSource!.MediaItem!.PublicId != null && s.MediaSource.MediaItem.RemovedAt == null
                && (s.MediaSource.MediaItem.Kind == MediaKind.Movie || s.MediaSource.MediaItem.Kind == MediaKind.Episode));
            var matching = all ? video : video.Where(_ => false);
            foreach (var condition in sources)
            {
                var match = SourceCondition(video, condition);
                matching = all ? matching.Where(s => match.Select(m => m.Id).Contains(s.Id)) : matching.Union(match);
            }
            var sourceItems = matching.Select(s => s.MediaSource!.MediaItem!.SeriesId ?? s.MediaSource.MediaItemId);
            var candidates = Items(catalogType).Where(i => sourceItems.Contains(i.Id));
            result = all ? result.Where(i => sourceItems.Contains(i.Id)) : result.Union(candidates);
        }
        foreach (var condition in rules.Conditions.Where(c => c.Field is not ("resolution" or "hdr")))
        {
            var candidates = TitleCondition(condition, catalogType);
            result = all ? result.Where(i => candidates.Select(m => m.Id).Contains(i.Id)) : result.Union(candidates);
        }
        return result;
    }

    private IQueryable<MediaItem> TitleCondition(GroupCondition condition, string catalogType)
    {
        if (condition.Field is "tag" or "genre")
        {
            var kind = condition.Field == "tag" ? MetadataTagKind.Keyword : MetadataTagKind.Genre;
            var value = condition.Value.Trim();
            return Items(catalogType).Where(i => database.MetadataRecords.Any(r => r.MediaItemId == i.Id && database.MetadataTags.Any(t =>
                t.MetadataRecordId == r.Id && t.Kind == kind && EF.Functions.Collate(t.Value, "NOCASE") == value)));
        }
        var year = int.Parse(condition.Value);
        var end = condition.EndValue is null ? year : int.Parse(condition.EndValue);
        var lower = condition.Operator switch { "before" => 1, "after" => year + 1, _ => year };
        var upper = condition.Operator switch { "before" => year - 1, "after" => 9998, "between" => end, _ => year };
        var from = new DateTimeOffset(lower, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var until = new DateTimeOffset(upper + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var preferred = settings.PreferredLanguage.ToLowerInvariant();
        var primary = preferred.Split('-')[0];
        return Items(catalogType).Where(i => i.Year != null ? i.Year >= lower && i.Year <= upper : database.MetadataRecords.Any(r =>
            r.MediaItemId == i.Id && r.Id == database.MetadataRecords.Where(m => m.MediaItemId == i.Id)
                .OrderBy(m => m.Language.ToLower() == preferred ? 0 : m.Language.ToLower() == primary || m.Language.ToLower().StartsWith(primary + "-") ? 1 : 2)
                .ThenBy(m => m.Id).Select(m => m.Id).FirstOrDefault()
            && r.ReleaseDate >= from && r.ReleaseDate < until));
    }

    private static IQueryable<MediaStream> SourceCondition(IQueryable<MediaStream> video, GroupCondition condition)
    {
        if (condition.Field == "resolution")
        {
            // Same ordered buckets as VideoResolution.Label; width handles cropped widescreen sources.
            return video.Where(s => ((s.Width ?? 0) >= 3800 || (s.Height ?? 0) >= 2000 ? "2160p"
                : (s.Width ?? 0) >= 1900 || (s.Height ?? 0) >= 1000 ? "1080p"
                : (s.Width ?? 0) >= 1260 || (s.Height ?? 0) >= 700 ? "720p"
                : (s.Width ?? 0) >= 700 || (s.Height ?? 0) >= 480 ? "480p" : "unknown") == condition.Value);
        }
        var formats = condition.Value == "any" ? HdrFormats.Where(f => f != "any").ToArray() : [condition.Value];
        var result = video.Where(_ => false);
        foreach (var format in formats)
        {
            var token = "·" + format.Replace(" ", "").ToUpperInvariant() + "·";
            result = result.Union(video.Where(s => s.HdrFormat != null &&
                ("·" + s.HdrFormat.Replace(" ", "").Replace(",", "·").ToUpper() + "·").Contains(token)));
        }
        return result;
    }

    /// <summary>Rejects unsupported rule versions, fields, values, or malformed type combinations.</summary>
    public static void Validate(SaveGroupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
            throw new GroupValidationException("Enter a group name of 1–120 characters.");
        if (request.CatalogType is not ("movie" or "series" or "anime"))
            throw new GroupValidationException("Choose Movies, Series, or Anime for this group.");
        if (request.Kind is not ("manual" or "smart") || request.Rules is null || request.MemberIds is null)
            throw new GroupValidationException("Choose a manual or smart group with a complete definition.");
        var rules = request.Rules;
        if (rules.Version != 1 || rules.Match is not ("all" or "any") || rules.Conditions is null || rules.Conditions.Count > 20)
            throw new GroupValidationException("Use version 1 rules with all/any matching and at most 20 conditions.");
        if (request.MemberIds.Count > 10000 || (request.Kind == "smart" && (rules.Conditions.Count == 0 || request.MemberIds.Count != 0))
            || (request.Kind == "manual" && rules.Conditions.Count != 0))
            throw new GroupValidationException("Manual groups contain title IDs; smart groups contain at least one condition.");
        foreach (var c in rules.Conditions)
        {
            if (c is null || string.IsNullOrWhiteSpace(c.Value) || c.Value.Length > 200)
                throw new GroupValidationException("Every condition needs a value of 1–200 characters.");
            var valid = c.Field switch
            {
                "year" => c.Operator is "before" or "after" or "equals" or "between"
                    && int.TryParse(c.Value, out var year) && year is >= 2 and <= 9997
                    && (c.Operator == "between" ? int.TryParse(c.EndValue, out var end) && end >= year && end <= 9998 : c.EndValue is null),
                "resolution" => c.Operator == "equals" && Resolutions.Contains(c.Value) && c.EndValue is null,
                "hdr" => c.Operator == "equals" && HdrFormats.Contains(c.Value) && c.EndValue is null,
                "tag" or "genre" => c.Operator == "contains" && c.EndValue is null,
                _ => false,
            };
            if (!valid) throw new GroupValidationException("Unsupported field, operator, or value in a group condition.");
        }
    }
}
