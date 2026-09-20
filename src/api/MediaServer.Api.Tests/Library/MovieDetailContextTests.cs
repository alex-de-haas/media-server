using Imposter.Abstractions;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: GenerateImposter(typeof(ITmdbRecommendationSource))]

namespace MediaServer.Api.Tests.Library;

public sealed class MovieDetailContextTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private MediaServerDbContext Db => _db.Context;
    private readonly Catalog _catalog;
    private readonly AppUser _me;
    private readonly AppUser _other;
    private readonly MediaItem _movie;
    private static readonly CancellationToken Ct = CancellationToken.None;
    private UserDataService UserData => new(Db, TimeProvider.System, new WatchHistoryRecorder(Db, TimeProvider.System));
    private LibraryReadService Library => new(Db, UserData, TestSettings.English);
    private LibraryDeleteService Deletes => new(Db, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));

    public MovieDetailContextTests()
    {
        _catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Root = "/movies", Type = CatalogType.Movie };
        _me = new AppUser { HostUserId = "me", DisplayName = "Me" };
        _other = new AppUser { HostUserId = "other", DisplayName = "Other" };
        Db.Catalogs.Add(_catalog);
        Db.AppUsers.AddRange(_me, _other);
        _movie = Movie("10", "Seed");
        Db.SaveChanges();
    }

    private MediaItem Movie(string identity, string title, bool removed = false, Guid? id = null)
    {
        var movie = new MediaItem { Id = id ?? Guid.NewGuid(), CatalogId = _catalog.Id, Kind = MediaKind.Movie,
            Title = title, IdentityProvider = "tmdb", IdentityProviderId = identity,
            PublicId = removed ? null : Guid.NewGuid().ToString(), RemovedAt = removed ? DateTimeOffset.UtcNow : null };
        Db.MediaItems.Add(movie);
        return movie;
    }

    private PlaybackHistoryEntry Watch(MediaItem movie, int userId, DateTimeOffset? instant)
    {
        var entry = new PlaybackHistoryEntry { Id = Guid.NewGuid(), AppUserId = userId, MediaItemId = movie.Id,
            WatchedAt = instant, Origin = PlaybackHistoryOrigin.Manual };
        Db.PlaybackHistoryEntries.Add(entry);
        return entry;
    }

    private void RemoveSeed()
    {
        _movie.PublicId = null;
        _movie.RemovedAt = DateTimeOffset.UtcNow;
        _movie.CatalogId = null;
    }

    [Fact]
    public async Task History_pages_preserve_rewatches_and_undated_marks_without_other_users()
    {
        var at = DateTimeOffset.Parse("2026-08-04T22:30:00Z");
        var first = Watch(_movie, _me.Id, at);
        var second = Watch(_movie, _me.Id, at);
        var earlier = Watch(_movie, _me.Id, at.AddDays(-1));
        var unknown = Watch(_movie, _me.Id, null);
        Watch(_movie, _other.Id, at);
        Watch(Movie("11", "Other movie"), _me.Id, at);
        await Db.SaveChangesAsync();
        var history = new MovieWatchHistoryService(Db);
        var page1 = await history.LoadAsync(_me.Id, _movie.Id, 0, 1, Ct);
        var page2 = await history.LoadAsync(_me.Id, _movie.Id, 1, 1, Ct);
        var page3 = await history.LoadAsync(_me.Id, _movie.Id, 2, 1, Ct);
        Assert.Equal(4, page1!.Total);
        Assert.Equal(3, page1.DatedTotal);
        Assert.Equal(unknown.Id, Assert.Single(page1.Undated).Id);
        Assert.NotEqual(Assert.Single(page1.Entries).Id, Assert.Single(page2!.Entries).Id);
        Assert.Contains(page1.Entries[0].Id, new[] { first.Id, second.Id });
        Assert.Equal(earlier.Id, Assert.Single(page3!.Entries).Id);
        Assert.Empty((await history.LoadAsync(_me.Id, _movie.Id, 3, 1, Ct))!.Entries);
    }

    [Theory]
    [InlineData(-1, 20, "offset")]
    [InlineData(0, 0, "limit")]
    [InlineData(0, 101, "limit")]
    public async Task History_rejects_invalid_page_bounds(int offset, int limit, string parameter)
    {
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new MovieWatchHistoryService(Db).LoadAsync(_me.Id, _movie.Id, offset, limit, Ct));
        Assert.Equal(parameter, error.ParamName);
    }

    [Fact]
    public async Task Removed_detail_requires_own_signal_and_explicit_web_opt_in()
    {
        RemoveSeed();
        Watch(_movie, _me.Id, null);
        Db.MetadataRecords.Add(new MetadataRecord { Id = Guid.NewGuid(), MediaItemId = _movie.Id,
            Provider = "tmdb", Language = "en-US", Overview = "Retained synopsis", Raw = "{}" });
        var actor = new Person { Id = Guid.NewGuid(), Provider = "tmdb", ProviderId = "123", Name = "Retained actor" };
        Db.Persons.Add(actor);
        Db.MediaItemPersons.Add(new MediaItemPerson { Id = Guid.NewGuid(), MediaItemId = _movie.Id,
            PersonId = actor.Id, Role = PersonRole.Cast });
        await Db.SaveChangesAsync();
        Assert.Null(await Library.GetDetailAsync(_movie.Id, _me.Id, Ct));
        Assert.Null(await Library.GetDetailAsync(_movie.Id, _other.Id, Ct, includeRemoved: true));
        Assert.Null(await Library.GetDetailAsync(_movie.Id, null, Ct, includeRemoved: true));
        var detail = await Library.GetDetailAsync(_movie.Id, _me.Id, Ct, includeRemoved: true);
        Assert.NotNull(detail);
        Assert.NotNull(detail.RemovedAt);
        Assert.Null(detail.CatalogId);
        Assert.Null(detail.PublicId);
        Assert.Empty(detail.MediaSources);
        Assert.Equal("Retained synopsis", detail.Overview);
        Assert.Equal("Retained actor", Assert.Single(detail.Cast).Name);
        Assert.Null(await new MovieWatchHistoryService(Db).LoadAsync(_other.Id, _movie.Id, 0, 20, Ct));
        Assert.Empty(await Library.ListAsync(null, MediaKind.Movie, _me.Id, Ct));
    }

    [Fact]
    public async Task Removed_personal_writes_do_not_broaden_playback_or_foreign_access()
    {
        RemoveSeed();
        Watch(_movie, _me.Id, null);
        await Db.SaveChangesAsync();
        Assert.Equal(SetRatingStatus.ItemNotFound, (await UserData.SetRatingAsync(_me.Id, _movie.Id, 4, Ct)).Status);
        Assert.Equal(SetRatingStatus.ItemNotFound, (await UserData.SetRatingAsync(_other.Id, _movie.Id, 4, Ct, true)).Status);
        Assert.Null(await UserData.SetFavoriteAsync(_other.Id, _movie.Id, true, Ct, true));
        Assert.Equal(LogWatchStatus.ItemNotFound, (await UserData.LogWatchAsync(_other.Id, _movie.Id, DateTimeOffset.UtcNow, Ct, true)).Status);
        Assert.Equal(SetRatingStatus.OutOfRange, (await UserData.SetRatingAsync(_me.Id, _movie.Id, 6, Ct, true)).Status);
        Assert.Equal(SetRatingStatus.Applied, (await UserData.SetRatingAsync(_me.Id, _movie.Id, 4, Ct, true)).Status);
        Assert.True((await UserData.SetFavoriteAsync(_me.Id, _movie.Id, true, Ct, true))!.IsFavorite);
        Assert.Null(await UserData.SetPlayedAsync(_me.Id, _movie.Id, true, null, Ct));
        Assert.Equal(LogWatchStatus.Recorded, (await UserData.LogWatchAsync(_me.Id, _movie.Id, DateTimeOffset.UtcNow.AddDays(-1), Ct, true)).Status);
        Assert.Equal(2, (await new MovieWatchHistoryService(Db).LoadAsync(_me.Id, _movie.Id, 0, 20, Ct))!.Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clearing_last_signal_hides_own_page_and_preserves_other_users(bool anotherUser)
    {
        RemoveSeed();
        var entry = Watch(_movie, _me.Id, DateTimeOffset.UtcNow.AddDays(-1));
        if (anotherUser) Watch(_movie, _other.Id, null);
        await Db.SaveChangesAsync();
        var service = new WatchHistoryEntryService(Db, new WatchHistoryRecorder(Db, TimeProvider.System), Deletes, TimeProvider.System);
        Assert.True(await service.DeleteAsync(_me.Id, entry.Id, Ct));
        Assert.Null(await Library.GetDetailAsync(_movie.Id, _me.Id, Ct, true));
        Assert.Equal(anotherUser, await Db.MediaItems.AnyAsync(item => item.Id == _movie.Id));
    }

    [Fact]
    public async Task Revival_restores_the_same_detail_route_and_personal_rating()
    {
        RemoveSeed();
        Db.UserItemData.Add(new UserItemData { AppUserId = _me.Id, MediaItemId = _movie.Id, Rating = 4 });
        await Db.SaveChangesAsync();
        Assert.NotNull((await Library.GetDetailAsync(_movie.Id, _me.Id, Ct, true))!.RemovedAt);
        _movie.RemovedAt = null; _movie.PublicId = "revived"; _movie.CatalogId = _catalog.Id;
        await Db.SaveChangesAsync();
        var restored = await Library.GetDetailAsync(_movie.Id, _me.Id, Ct, true);
        Assert.Null(restored!.RemovedAt);
        Assert.Equal(4, restored.UserData!.UserRating);
    }

    [Fact]
    public async Task Related_rows_keep_available_movies_provider_order_and_single_collection_sibling()
    {
        var collection = new MovieCollection { Id = Guid.NewGuid(), Provider = "tmdb", ProviderId = "100", Name = "The Collection" };
        Db.MovieCollections.Add(collection);
        _movie.CollectionId = collection.Id;
        RemoveSeed();
        Watch(_movie, _me.Id, null);
        var sibling = Movie("11", "Sibling"); sibling.CollectionId = collection.Id;
        var otherCatalog = new Catalog { Id = Guid.NewGuid(), Name = "Other catalog", Root = "/other", Type = CatalogType.Movie };
        Db.Catalogs.Add(otherCatalog);
        var duplicateSibling = Movie("11", "Legacy copy without collection link"); duplicateSibling.CatalogId = otherCatalog.Id;
        var duplicateCandidate = Movie("12", "Legacy candidate copy", id: Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")); duplicateCandidate.CatalogId = otherCatalog.Id;
        var first = Movie("12", "First recommendation");
        var second = Movie("13", "Only similar"); second.IdentityProvider = "imdb"; second.Providers = new() { ["tmdb"] = "13" };
        var removed = Movie("14", "Removed", true);
        var unpublished = Movie("15", "Unpublished"); unpublished.PublicId = null;
        var series = Movie("16", "TV id collision"); series.Kind = MediaKind.Series;
        Watch(first, _me.Id, DateTimeOffset.UtcNow);
        Db.UserItemData.Add(new UserItemData { AppUserId = _me.Id, MediaItemId = first.Id, Rating = 5, IsFavorite = true });
        await Db.SaveChangesAsync();
        var source = ITmdbRecommendationSource.Imposter();
        source.ForSeedAsync(Arg<RecommendationIdentity>.Any(), Arg<TmdbRecommendationGenerator>.Any(), Arg<CancellationToken>.Any())
            .Returns((RecommendationIdentity identity, TmdbRecommendationGenerator generator, CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<TmdbRecommendedTitle>>(generator == TmdbRecommendationGenerator.Seeds
                    ? [Title("10"), Title("11"), Title("12"), Title("12"), Title("14"), Title("15"), Title("16"), Title("99")]
                    : [Title("12"), Title("13")]));
        var service = new RelatedMoviesService(Db, Library, source.Instance());
        var franchise = await service.CollectionAsync(_movie.Id, _me.Id, Ct);
        Assert.Equal("The Collection", franchise!.CollectionName);
        Assert.Equal(sibling.Id, Assert.Single(franchise.Items).Id);
        var similar = await service.SimilarAsync(_movie.Id, _me.Id, Ct);
        Assert.Equal(new[] { first.Id, second.Id }, similar!.Items.Select(item => item.Id));
        Assert.Equal(5, similar.Items[0].UserData!.UserRating);
        Assert.Null(await service.SimilarAsync(_movie.Id, _other.Id, Ct));
    }

    [Fact]
    public async Task Similar_movies_ignore_collection_members_without_tmdb_identity()
    {
        var collection = new MovieCollection { Id = Guid.NewGuid(), Provider = "tmdb", ProviderId = "100", Name = "Saga" };
        Db.MovieCollections.Add(collection);
        _movie.CollectionId = collection.Id;
        var unknown = Movie("11", "Unknown identity");
        unknown.CollectionId = collection.Id;
        unknown.IdentityProviderId = null;
        var candidate = Movie("12", "Recommendation");
        await Db.SaveChangesAsync();
        var source = ITmdbRecommendationSource.Imposter();
        source.ForSeedAsync(Arg<RecommendationIdentity>.Any(), Arg<TmdbRecommendationGenerator>.Any(), Arg<CancellationToken>.Any())
            .Returns(Task.FromResult<IReadOnlyList<TmdbRecommendedTitle>>([Title("12")]));
        var service = new RelatedMoviesService(Db, Library, source.Instance());

        Assert.Equal(unknown.Id, Assert.Single((await service.CollectionAsync(_movie.Id, _me.Id, Ct))!.Items).Id);
        Assert.Equal(candidate.Id, Assert.Single((await service.SimilarAsync(_movie.Id, _me.Id, Ct))!.Items).Id);
    }

    [Fact]
    public async Task Collection_uses_release_order_and_works_without_a_provider_request()
    {
        var collection = new MovieCollection { Id = Guid.NewGuid(), Provider = "tmdb", ProviderId = "100", Name = "Saga" };
        Db.MovieCollections.Add(collection); _movie.CollectionId = collection.Id;
        var later = Movie("11", "B"); later.CollectionId = collection.Id; later.Year = 2020;
        var early = Movie("12", "A"); early.CollectionId = collection.Id; early.Year = 1999;
        var unknown = Movie("13", "Unknown"); unknown.CollectionId = collection.Id;
        _movie.IdentityProviderId = null;
        await Db.SaveChangesAsync();
        var source = ITmdbRecommendationSource.Imposter();
        source.ForSeedAsync(Arg<RecommendationIdentity>.Any(), Arg<TmdbRecommendationGenerator>.Any(), Arg<CancellationToken>.Any())
            .Returns((RecommendationIdentity i, TmdbRecommendationGenerator g, CancellationToken c) => throw new InvalidOperationException("Must not fetch"));
        var service = new RelatedMoviesService(Db, Library, source.Instance());
        Assert.Equal(new[] { early.Id, later.Id, unknown.Id }, (await service.CollectionAsync(_movie.Id, _me.Id, Ct))!.Items.Select(item => item.Id));
        Assert.Empty((await service.SimilarAsync(_movie.Id, _me.Id, Ct))!.Items);
    }

    [Theory]
    [InlineData("rating", false)]
    [InlineData("rating", true)]
    [InlineData("favorite", false)]
    [InlineData("favorite", true)]
    public async Task Personal_web_routes_purge_only_after_every_users_last_signal(string mark, bool otherSignal)
    {
        RemoveSeed();
        Db.UserItemData.Add(new UserItemData { Id = Guid.NewGuid(), AppUserId = _me.Id, MediaItemId = _movie.Id,
            Rating = mark == "rating" ? 4 : null, IsFavorite = mark == "favorite" });
        if (otherSignal) Watch(_movie, _other.Id, null);
        await Db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "me")]));
        var result = mark == "rating"
            ? await LibraryEndpoints.SetRatingAsync(_movie.Id, null, principal, UserData, Db, Deletes, Ct)
            : await LibraryEndpoints.SetFavoriteAsync(_movie.Id, false, principal, UserData, Db, Deletes, Ct);
        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Null(await Library.GetDetailAsync(_movie.Id, _me.Id, Ct, true));
        Assert.Equal(otherSignal, await Db.MediaItems.AnyAsync(item => item.Id == _movie.Id));
    }

    private static TmdbRecommendedTitle Title(string id) => new(id, id, null, null);
    public void Dispose() => _db.Dispose();
}
