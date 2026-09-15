using System.Data.Common;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Groups;
using MediaServer.Api.Library;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MediaServer.Api.Tests.Groups;

public sealed class GroupServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly MediaServerDbContext db;
    private readonly GroupService service;
    private readonly CancellationToken ct = CancellationToken.None;
    private readonly ReadCommands reads = new();
    public GroupServiceTests()
    {
        connection.Open();
        db = new(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(connection).AddInterceptors(reads).Options);
        db.Database.Migrate();
        var settings = new MediaServerSettings { SupportedLanguages = ["en-US"] };
        service = new(db, new LibraryReadService(db, new UserDataService(db, TimeProvider.System), settings), settings);
    }
    private MediaItem Title(string name, int? year = 1999, CatalogType type = CatalogType.Movie)
    {
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = name, Type = type, Root = "/test/" + Guid.NewGuid() };
        db.Add(catalog);
        var item = new MediaItem { Id = Guid.NewGuid(), Title = name, Year = year, Kind = type == CatalogType.Movie ? MediaKind.Movie : MediaKind.Series,
            CatalogId = catalog.Id, PublicId = Guid.NewGuid().ToString("N") };
        db.Add(item); db.SaveChanges(); return item;
    }
    private MediaSource Source(MediaItem item, string? hdr, int width = 3840, int height = 1600)
    {
        var source = new MediaSource { Id = Guid.NewGuid(), MediaItemId = item.Id, Container = "mkv", Path = "test.mkv" };
        source.Streams.Add(new MediaStream { Id = Guid.NewGuid(), StreamType = StreamType.Video, Width = width, Height = height, HdrFormat = hdr });
        db.Add(source); db.SaveChanges(); return source;
    }
    private MetadataRecord Metadata(MediaItem item, int year, string language = "en-US", params (MetadataTagKind Kind, string Value)[] tags)
    {
        var record = new MetadataRecord { Id = Guid.NewGuid(), MediaItemId = item.Id, Provider = "tmdb", Language = language,
            ReleaseDate = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero), Title = item.Title };
        db.Add(record);
        db.AddRange(tags.Select(t => new MetadataTag { Id = Guid.NewGuid(), MetadataRecordId = record.Id, Kind = t.Kind, Value = t.Value }));
        db.SaveChanges(); return record;
    }
    private static SaveGroupRequest Smart(string type = "movie", string match = "all", params GroupCondition[] conditions) =>
        new("Smart", "smart", type, new(1, match, conditions), []);
    private static SaveGroupRequest Manual(params Guid[] ids) => new("Manual", "manual", "movie", new(1, "all", []), ids);
    private static GroupCondition Hdr(string value) => new("hdr", "equals", value);
    private async Task<LibrarySearchPage> Preview(params GroupCondition[] conditions) => await service.PreviewAsync(Smart(conditions: conditions), null, 60, 0, ct);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(12, 2)]
    [InlineData(65, 4)]
    public async Task List_batches_counts_and_preserves_order_empty_groups_and_unique_members(int groupCount, int expectedReads)
    {
        var movie = Title("Retro Film"); Source(movie, "HDR10"); Source(movie, "HDR10");
        var hidden = Title("Removed"); hidden.RemovedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        for (var index = groupCount - 1; index >= 0; index--)
        {
            var request = (index % 3) switch
            {
                0 => Manual(movie.Id, hidden.Id),
                1 => Smart(match: "any", conditions: [Hdr("any"), new("year", "before", "2000")]),
                _ => Smart(conditions: [new("year", "after", "2000")]),
            };
            await service.SaveAsync(null, request with { Name = $"Group {index:D3}" }, ct);
        }
        reads.Commands.Clear();

        var groups = await service.ListAsync(ct);

        Assert.Equal(expectedReads, reads.Commands.Count);
        Assert.Equal(groupCount, groups.Count);
        for (var index = 0; index < groupCount; index++)
        {
            Assert.Equal($"Group {index:D3}", groups[index].Name);
            Assert.Equal(index % 3 == 2 ? 0 : 1, groups[index].ItemCount);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_candidate_search_does_not_add_a_title_predicate(string? search)
    {
        var movie = Title("Retro Film");
        reads.Commands.Clear();
        var page = await service.CandidatesAsync("movie", search, null, 30, 0, ct);
        Assert.Equal(movie.Id, Assert.Single(page.Items).Id);
        Assert.DoesNotContain("LIKE", reads.Commands[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("instr(", reads.Commands[0], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(" retro ")]
    [InlineData("RETRO")]
    public async Task Candidate_search_matches_item_and_metadata_titles_without_case_sensitivity(string search)
    {
        var direct = Title("Retro Film");
        var translated = Title("Different title"); Metadata(translated, 1999).Title = "Retro Translation";
        Title("Unrelated"); await db.SaveChangesAsync(ct);
        var page = await service.CandidatesAsync("movie", search, null, 30, 0, ct);
        Assert.Equal(2, page.Total);
        Assert.Equal(new[] { direct.Id, translated.Id }.Order(), page.Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task Option_search_matches_keywords_and_genres_without_case_sensitivity()
    {
        Metadata(Title("Film"), 1999, "en-US", (MetadataTagKind.Keyword, "heist"), (MetadataTagKind.Genre, "Heist Comedy"));
        var options = await service.OptionsAsync("movie", " HeIsT ", ct);
        Assert.Equal("heist", Assert.Single(options.Tags));
        Assert.Equal("Heist Comedy", Assert.Single(options.Genres));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    public async Task Searches_treat_like_wildcards_and_escape_characters_literally(string literal)
    {
        var direct = Title("Film " + literal);
        var translated = Title("Translation"); Metadata(translated, 1999).Title = "Film " + literal;
        Metadata(direct, 1999, "en-US", (MetadataTagKind.Keyword, "tag" + literal), (MetadataTagKind.Genre, "genre" + literal));
        Metadata(Title("Unrelated"), 1999, "en-US", (MetadataTagKind.Keyword, "tag-other"), (MetadataTagKind.Genre, "genre-other"));
        await db.SaveChangesAsync(ct);
        Assert.Equal(2, (await service.CandidatesAsync("movie", literal, null, 30, 0, ct)).Total);
        var options = await service.OptionsAsync("movie", literal, ct);
        Assert.Equal("tag" + literal, Assert.Single(options.Tags));
        Assert.Equal("genre" + literal, Assert.Single(options.Genres));
    }

    [Theory]
    [InlineData("HDR10")]
    [InlineData("HDR10+")]
    [InlineData("Dolby Vision")]
    [InlineData("HLG")]
    [InlineData("HDR")]
    public async Task Every_hdr_type_matches_itself_and_any_hdr_once(string format)
    {
        var title = Title("Matching"); Source(title, format); Source(title, format);
        Source(Title("SDR"), null); Source(Title("Unknown"), "Unknown");
        var exact = await Preview(Hdr(format));
        Assert.Equal(title.Id, Assert.Single(exact.Items).Id); Assert.Equal(1, exact.Total);
        Assert.Equal(title.Id, Assert.Single((await Preview(Hdr("any"))).Items).Id);
    }

    [Fact]
    public async Task Hdr_tokens_are_exact_and_combined_tokens_match_both()
    {
        Source(Title("Plus"), "HDR10+"); Source(Title("Unspecified"), "HDR");
        var both = Title("Both"); Source(both, " hdr10 , Dolby Vision ");
        Assert.Equal(both.Id, Assert.Single((await Preview(Hdr("HDR10"))).Items).Id);
        Assert.Equal(both.Id, Assert.Single((await Preview(Hdr("Dolby Vision"))).Items).Id);
        Assert.Equal(3, (await Preview(Hdr("any"))).Total);
    }

    [Fact]
    public async Task All_source_conditions_match_one_source_and_any_can_match_different_sources()
    {
        var title = Title("Split"); Source(title, null); Source(title, "Dolby Vision", 1920, 816);
        var resolution = new GroupCondition("resolution", "equals", "2160p");
        Assert.Empty((await Preview(resolution, Hdr("Dolby Vision"))).Items);
        Assert.Single((await service.PreviewAsync(Smart(match: "any", conditions: [resolution, Hdr("Dolby Vision")]), null, 60, 0, ct)).Items);
        var matching = Source(title, "Dolby Vision");
        var saved = (await service.SaveAsync(null, Smart(conditions: [resolution, Hdr("Dolby Vision")]), ct))!;
        Assert.Single((await service.GetAsync(saved.Id, null, 60, 0, ct))!.Items);
        db.Remove(matching); await db.SaveChangesAsync(ct);
        Assert.Empty((await service.GetAsync(saved.Id, null, 60, 0, ct))!.Items);
    }

    [Fact]
    public async Task Year_boundary_metadata_fallback_and_cross_catalog_overlap()
    {
        var retro = Title("Retro"); Source(retro, "HDR10");
        Title("Boundary", 2000); Title("Unknown", null);
        var fallback = Title("Fallback", null); Metadata(fallback, 1990, "en-GB"); Metadata(fallback, 2001, "ru-RU");
        var rule = new GroupCondition("year", "before", "2000");
        var old = (await service.SaveAsync(null, Smart(conditions: [rule]), ct))!;
        var hdr = (await service.SaveAsync(null, Smart(conditions: [Hdr("any")]), ct))!;
        var details = (await service.GetAsync(old.Id, null, 1, 0, ct))!;
        Assert.Equal(2, details.Total); Assert.Single(details.Items);
        var next = (await service.GetAsync(old.Id, null, 1, 1, ct))!;
        Assert.NotEqual(details.Items[0].Id, next.Items[0].Id);
        Assert.Equal(retro.Id, Assert.Single((await service.GetAsync(hdr.Id, null, 60, 0, ct))!.Items).Id);
        Assert.Equal(2, (await service.ListAsync(ct)).Single(g => g.Id == old.Id).ItemCount);
    }

    [Fact]
    public async Task Tags_and_genres_match_whole_values_and_refresh_updates_membership()
    {
        var title = Title("Heist comedy");
        Metadata(title, 1999, "en-US", (MetadataTagKind.Keyword, "heist"), (MetadataTagKind.Genre, "Comedy"), (MetadataTagKind.Genre, "Action"));
        var tag = new GroupCondition("tag", "contains", "HEIST");
        var genre = new GroupCondition("genre", "contains", "comedy");
        Assert.Single((await Preview(tag, genre)).Items);
        Assert.Empty((await Preview(new GroupCondition("tag", "contains", "Comedy"))).Items);
        Assert.Empty((await Preview(new GroupCondition("tag", "contains", "heis"))).Items);
        Assert.Single((await Preview(genre, new("genre", "contains", "Action"))).Items);
        var saved = (await service.SaveAsync(null, Smart(conditions: [tag, genre]), ct))!;
        db.Remove(await db.MetadataTags.SingleAsync(t => t.Kind == MetadataTagKind.Keyword)); await db.SaveChangesAsync(ct);
        Assert.Equal(0, (await service.GetAsync(saved.Id, null, 60, 0, ct))!.Total);
        var options = await service.OptionsAsync("movie", "Com", ct);
        Assert.Contains("Comedy", options.Genres); Assert.Empty(options.Tags);
    }

    [Fact]
    public async Task Series_and_anime_are_separate_and_matching_episodes_roll_up_once()
    {
        foreach (var type in new[] { CatalogType.Series, CatalogType.Anime })
        {
            var series = Title(type.ToString(), type: type);
            for (var n = 0; n < 2; n++)
            {
                var episode = new MediaItem { Id = Guid.NewGuid(), Title = "Episode", Kind = MediaKind.Episode,
                    ParentId = series.Id, SeriesId = series.Id, CatalogId = series.CatalogId, PublicId = Guid.NewGuid().ToString() };
                db.Add(episode); db.SaveChanges(); Source(episode, "HLG");
            }
        }
        foreach (var type in new[] { "series", "anime" })
        {
            var page = await service.PreviewAsync(Smart(type, conditions: [Hdr("HLG")]), null, 60, 0, ct);
            Assert.Equal(1, page.Total); Assert.Equal("Series", Assert.Single(page.Items).Kind);
            Assert.Equal(type, page.Items[0].Title.ToLowerInvariant());
        }
        Assert.Empty((await Preview(Hdr("HLG"))).Items);
    }

    [Fact]
    public async Task Manual_membership_is_idempotent_survives_removal_and_group_delete_keeps_titles()
    {
        var movie = Title("Manual movie");
        var input = Manual(movie.Id, movie.Id);
        var saved = (await service.SaveAsync(null, input, ct))!;
        await service.SaveAsync(saved.Id, input, ct);
        Assert.Single(await db.Set<MediaGroupMember>().ToListAsync(ct));
        movie.RemovedAt = DateTimeOffset.UtcNow; movie.PublicId = null; await db.SaveChangesAsync(ct);
        Assert.Equal(0, (await service.GetAsync(saved.Id, null, 60, 0, ct))!.Total);
        Assert.Single((await service.DefinitionAsync(saved.Id, ct))!.MemberIds);
        movie.RemovedAt = null; movie.PublicId = "revived"; await db.SaveChangesAsync(ct);
        Assert.Single((await service.GetAsync(saved.Id, null, 60, 0, ct))!.Items);
        Assert.True(await service.DeleteAsync(saved.Id, ct));
        Assert.NotNull(await db.MediaItems.FindAsync(movie.Id));
        Assert.Empty(await db.Set<MediaGroupMember>().ToListAsync(ct));
        Assert.Null(await service.GetAsync(saved.Id, null, 60, 0, ct));
        var empty = (await service.SaveAsync(null, Manual(), ct))!;
        Assert.Equal(0, (await service.ListAsync(ct)).Single(g => g.Id == empty.Id).ItemCount);
    }

    [Fact]
    public async Task Manual_groups_reject_other_catalog_types_and_type_changes()
    {
        var series = Title("TV", type: CatalogType.Series);
        await Assert.ThrowsAsync<GroupValidationException>(() => service.SaveAsync(null, Manual(series.Id), ct));
        var saved = (await service.SaveAsync(null, Manual(), ct))!;
        await Assert.ThrowsAsync<GroupValidationException>(() => service.SaveAsync(saved.Id, Manual() with { CatalogType = "anime" }, ct));
    }

    [Fact]
    public async Task Existing_manual_links_survive_type_moves_and_do_not_block_editing()
    {
        var series = Title("Series", type: CatalogType.Series);
        var input = Manual(series.Id) with { CatalogType = "series" };
        var saved = (await service.SaveAsync(null, input, ct))!;
        var anime = Title("Anime", type: CatalogType.Anime);
        series.CatalogId = anime.CatalogId;
        await db.SaveChangesAsync(ct);
        Assert.Empty((await service.GetAsync(saved.Id, null, 60, 0, ct))!.Items);
        await service.SaveAsync(saved.Id, input with { Name = "Renamed" }, ct);
        Assert.Single((await service.DefinitionAsync(saved.Id, ct))!.MemberIds);
        db.Remove(series); await db.SaveChangesAsync(ct);
        Assert.Empty((await service.DefinitionAsync(saved.Id, ct))!.MemberIds);
    }

    [Fact]
    public async Task Shared_membership_preserves_per_viewer_played_state_and_franchise_link()
    {
        var movie = Title("Shared");
        var franchise = new MovieCollection { Id = Guid.NewGuid(), Provider = "tmdb", ProviderId = "42", Name = "Franchise" };
        db.Add(franchise); movie.CollectionId = franchise.Id;
        var viewer = new AppUser { HostUserId = "viewer" };
        var other = new AppUser { HostUserId = "other" };
        db.AddRange(viewer, other); await db.SaveChangesAsync(ct);
        db.Add(new UserItemData { AppUserId = viewer.Id, MediaItemId = movie.Id, Played = true });
        await db.SaveChangesAsync(ct);
        var saved = (await service.SaveAsync(null, Manual(movie.Id), ct))!;
        Assert.True(Assert.Single((await service.GetAsync(saved.Id, viewer.Id, 60, 0, ct))!.Items).UserData!.Played);
        Assert.False(Assert.Single((await service.GetAsync(saved.Id, other.Id, 60, 0, ct))!.Items).UserData!.Played);
        Assert.Equal(franchise.Id, movie.CollectionId);
        movie.CollectionId = null; await db.SaveChangesAsync(ct);
        Assert.Single((await service.GetAsync(saved.Id, null, 60, 0, ct))!.Items);
    }

    [Theory]
    [InlineData("year", "before", "abc")]
    [InlineData("year", "before", "1")]
    [InlineData("year", "equals", "10000")]
    [InlineData("hdr", "equals", "SDR")]
    [InlineData("genre", "contains", "")]
    [InlineData("unknown", "equals", "1")]
    public void Invalid_conditions_are_rejected(string field, string op, string value) =>
        Assert.Throws<GroupValidationException>(() => GroupService.Validate(Smart(conditions: [new(field, op, value)])));

    public void Dispose() { db.Dispose(); connection.Dispose(); }

    private sealed class ReadCommands : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
