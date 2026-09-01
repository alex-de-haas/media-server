using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// The windowed, searchable library read — the one an agent asks "do I have this?" through. What is
/// asserted is mostly that a "no" is trustworthy: the search reaches the title the caller would say
/// out loud, the total tells a full page from a complete answer, and watched state is evaluated the
/// way the library defines it rather than the way the column reads.
/// </summary>
public sealed class LibrarySearchTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly LibraryReadService _library;
    private readonly Guid _catalogId = Guid.NewGuid();
    private int _userId;

    public LibrarySearchTests()
    {
        _context = _db.Create();
        _context.Catalogs.Add(new Catalog
        {
            Id = _catalogId,
            Name = "Films",
            Type = CatalogType.Movie,
            Root = "/catalog",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var user = new AppUser { HostUserId = "operator@host", DisplayName = "Operator", CreatedAt = DateTimeOffset.UtcNow };
        _context.AppUsers.Add(user);
        _context.SaveChanges();
        _userId = user.Id;

        _library = new LibraryReadService(
            _context, new UserDataService(_context, TimeProvider.System),
            new MediaServerSettings { SupportedLanguages = ["en-US"] });
    }

    [Fact]
    public async Task A_film_is_found_by_the_title_it_shows_and_by_the_one_on_disk()
    {
        // The rendered title comes from the metadata record, while the item's own title is whatever the
        // release was called. Searching only the column would miss "Inception" for a file named
        // "Untitled.2010.1080p.BluRay" — the exact case the library's own fixture is built around.
        var id = AddMovie("Untitled.2010.1080p.BluRay", localized: "Inception");
        AddMovie("Some.Other.Release", localized: "Barbie");
        await _context.SaveChangesAsync();

        Assert.Equal(id, Assert.Single((await Search(title: "inception")).Items).Id);
        Assert.Equal(id, Assert.Single((await Search(title: "untitled"))
            .Items).Id);
        // Beside a term that must find nothing, so a search matching everything would fail here.
        Assert.Empty((await Search(title: "dune")).Items);
    }

    [Fact]
    public async Task The_total_counts_what_the_window_left_behind()
    {
        for (var i = 0; i < 5; i++)
        {
            AddMovie($"Release.{i}", localized: $"Film {i}");
        }

        await _context.SaveChangesAsync();

        var windowed = await Search(limit: 2);
        Assert.Equal(2, windowed.Items.Count);
        Assert.Equal(5, windowed.Total);

        var whole = await Search(limit: 50);
        Assert.Equal(5, whole.Items.Count);
        Assert.Equal(5, whole.Total);
    }

    [Fact]
    public async Task A_series_is_watched_when_its_episodes_are_and_not_before()
    {
        // A series has no played flag of its own — the state is a rollup over its episodes. Filtering on
        // the series row would call a fully-watched series unwatched, which is the answer that looks
        // plausible and sends the operator to re-watch something they finished.
        var finished = AddSeries("Finished", episodes: 2, watchedEpisodes: 2);
        var midway = AddSeries("Midway", episodes: 2, watchedEpisodes: 1);
        await _context.SaveChangesAsync();

        Assert.Equal(finished, Assert.Single((await Search(watched: true)).Items).Id);
        Assert.Equal(midway, Assert.Single((await Search(watched: false)).Items).Id);
    }

    [Fact]
    public async Task A_series_with_nothing_published_is_not_watched()
    {
        // Vacuous truth: "no unwatched episode" is true of a series with no episodes at all, so the
        // rollup has to require that something exists before it can be finished.
        AddSeries("Empty", episodes: 0, watchedEpisodes: 0);
        await _context.SaveChangesAsync();

        Assert.Empty((await Search(watched: true)).Items);
        Assert.Single((await Search(watched: false)).Items);
    }

    [Fact]
    public async Task Paging_returns_every_row_exactly_once()
    {
        var expected = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            // Deliberately one repeated title, so the ordering has a tie to break. Without one the pages
            // could not overlap however the query was written, and the test would prove nothing.
            expected.Add(AddMovie($"Release.{i}", localized: i < 3 ? "Same Title" : $"Film {i}"));
        }

        await _context.SaveChangesAsync();

        var seen = new List<Guid>();
        for (var offset = 0; offset < 6; offset += 2)
        {
            seen.AddRange((await Search(limit: 2, offset: offset)).Items.Select(item => item.Id));
        }

        Assert.Equal(6, seen.Distinct().Count());
        Assert.Equal(expected.OrderBy(id => id), seen.OrderBy(id => id));
    }

    [Fact]
    public async Task A_row_carries_what_a_suggestion_has_to_be_narrowed_by()
    {
        // "An unwatched comedy under two hours" is three fields. Without them on the row a caller has to
        // fetch every title one by one to answer it, which for a real library is not an option.
        var id = AddMovie("Comedy.Release", localized: "A Comedy", genres: ["Comedy"], runtimeTicks: 42_000_000_000, rating: 7.5);
        await _context.SaveChangesAsync();

        var row = Assert.Single((await Search(title: "comedy")).Items);

        Assert.Equal(id, row.Id);
        Assert.Equal(["Comedy"], row.Genres);
        Assert.Equal(42_000_000_000, row.RuntimeTicks);
        Assert.Equal(7.5, row.CommunityRating);
    }

    [Fact]
    public async Task What_a_film_is_about_is_matched_against_both_its_keywords_and_its_synopsis()
    {
        // The two sources fail in opposite directions, which is why both are searched. A keyword is
        // precise and sparse — TMDb keeps only a handful, so its absence is weak evidence — while the
        // synopsis is complete and vague. Either alone answers "something about a plane hijacking"
        // badly, so both are asserted here, separately.
        var byKeyword = AddTagged("Air Force One", keywords: ["aircraft hijacking"]);
        var bySynopsis = AddTagged("Con Air", overview: "Convicts seize a prisoner transport plane in mid-air.");
        AddTagged("Barbie", overview: "A doll goes to the real world.", keywords: ["toy"]);
        await _context.SaveChangesAsync();

        Assert.Equal(byKeyword, Assert.Single((await Search(about: "aircraft hijacking")).Items).Id);
        Assert.Equal(bySynopsis, Assert.Single((await Search(about: "prisoner transport")).Items).Id);
        Assert.Empty((await Search(about: "submarine")).Items);
    }

    [Fact]
    public async Task Several_genres_mean_all_of_them_and_not_any_of_them()
    {
        // "An action comedy" is one film that is both, not two films that are either. A single Any()
        // over the requested list would have quietly meant "any of" and returned three rows here, all
        // of them defensible-looking.
        var both = AddTagged("Rush Hour", genres: ["Action", "Comedy"]);
        AddTagged("Die Hard", genres: ["Action"]);
        AddTagged("Airplane!", genres: ["Comedy"]);
        await _context.SaveChangesAsync();

        Assert.Equal(both, Assert.Single((await Search(genres: ["Action", "Comedy"])).Items).Id);

        // Beside the single-genre case, so a filter that always required everything would fail too.
        Assert.Equal(2, (await Search(genres: ["Action"])).Items.Count);
    }

    private Task<LibrarySearchPage> Search(
        string? title = null, bool? watched = null, int? limit = null, int? offset = null,
        string? about = null, IReadOnlyList<string>? genres = null) =>
        _library.SearchAsync(
            new LibrarySearchQuery(
                Title: title, Watched: watched, About: about, Genres: genres,
                Limit: limit ?? 50, Offset: offset),
            _userId,
            CancellationToken.None);

    private Guid AddTagged(
        string title, string? overview = null, IReadOnlyList<string>? genres = null,
        IReadOnlyList<string>? keywords = null)
    {
        var id = AddMovie(title, localized: title, genres: genres);
        var record = _context.MetadataRecords.Local.Single(entry => entry.MediaItemId == id);
        record.Overview = overview;
        foreach (var genre in genres ?? [])
        {
            _context.MetadataTags.Add(new MetadataTag
            {
                Id = Guid.NewGuid(), MetadataRecordId = record.Id, Kind = MetadataTagKind.Genre, Value = genre,
            });
        }

        foreach (var keyword in keywords ?? [])
        {
            _context.MetadataTags.Add(new MetadataTag
            {
                Id = Guid.NewGuid(), MetadataRecordId = record.Id, Kind = MetadataTagKind.Keyword, Value = keyword,
            });
        }

        return id;
    }

    private Guid AddMovie(
        string rawTitle, string localized, IReadOnlyList<string>? genres = null,
        long? runtimeTicks = null, double? rating = null)
    {
        var id = Add(MediaKind.Movie, rawTitle);
        _context.MetadataRecords.Add(new MetadataRecord
        {
            Id = Guid.NewGuid(),
            MediaItemId = id,
            Provider = "tmdb",
            Language = "en-US",
            Title = localized,
            Genres = genres?.ToList() ?? [],
            RuntimeTicks = runtimeTicks,
            CommunityRating = rating,
            FetchedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    private Guid AddSeries(string title, int episodes, int watchedEpisodes)
    {
        var seriesId = Add(MediaKind.Series, title);
        for (var index = 0; index < episodes; index++)
        {
            var episodeId = Add(MediaKind.Episode, $"{title} E{index}", seriesId, parentId: seriesId);
            if (index < watchedEpisodes)
            {
                _context.UserItemData.Add(new UserItemData
                {
                    Id = Guid.NewGuid(),
                    AppUserId = _userId,
                    MediaItemId = episodeId,
                    Played = true,
                });
            }
        }

        return seriesId;
    }

    private Guid Add(MediaKind kind, string title, Guid? seriesId = null, Guid? parentId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = _catalogId,
            Kind = kind,
            Title = title,
            PublicId = Guid.NewGuid().ToString("N"),
            SeriesId = seriesId,
            ParentId = parentId,
            AddedAt = now,
            UpdatedAt = now,
        };
        _context.MediaItems.Add(item);
        return item.Id;
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }
}
