using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// The operator's override of which poster an item shows: the candidate list behind "Change poster", and
/// pinning one of them. Every candidate is already cached, so none of this reaches the provider.
/// </summary>
public sealed class ItemArtworkServiceTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly ItemArtworkService _artwork;
    private readonly Guid _itemId = Guid.NewGuid();

    public ItemArtworkServiceTests()
    {
        using (var context = _db.Create())
        {
            var now = DateTimeOffset.UtcNow;
            context.MediaItems.Add(new MediaItem
            {
                Id = _itemId,
                PublicId = Guid.NewGuid().ToString("N"),
                Kind = MediaKind.Movie,
                Title = "John Wick: Chapter 3",
                AddedAt = now,
                UpdatedAt = now,
            });
            context.ImageAssets.AddRange(
                Image(ImageType.Primary, null, 0, "textless"),
                Image(ImageType.Primary, "en", 1, "english"),
                Image(ImageType.Primary, "ru", 2, "russian"),
                Image(ImageType.Backdrop, null, 0, "backdrop"),
                Image(ImageType.Logo, "en", 0, "logo"));
            context.SaveChanges();
        }

        _context = _db.Create();
        _artwork = new ItemArtworkService(_context, TestSettings.For("ru-RU", "en-US"));
    }

    [Fact]
    public async Task Lists_every_cached_candidate_with_the_shown_one_marked()
    {
        var images = await _artwork.ListAsync(_itemId, CancellationToken.None);

        Assert.NotNull(images);
        Assert.Equal(5, images!.Count);
        var posters = images.Where(image => image.Type == nameof(ImageType.Primary)).ToList();
        // Ordered as the surfaces rank them, so the first entry is what the library is showing.
        Assert.Equal(["russian", "english", "textless"], posters.Select(image => image.Tag));
        Assert.Equal("russian", Assert.Single(posters, image => image.Selected).Tag);
        Assert.DoesNotContain(posters, image => image.Pinned);
        // The other roles are listed too, each ranked by its own rule: a backdrop wants no text.
        Assert.True(images.Single(image => image.Tag == "backdrop").Selected);
    }

    [Fact]
    public async Task An_unknown_item_has_no_artwork_to_list()
    {
        Assert.Null(await _artwork.ListAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Pinning_a_poster_makes_it_the_selected_one()
    {
        Assert.Equal(PinPosterResult.Ok, await _artwork.PinAsync(_itemId, "textless", CancellationToken.None));

        var images = await _artwork.ListAsync(_itemId, CancellationToken.None);
        var posters = images!.Where(image => image.Type == nameof(ImageType.Primary)).ToList();
        Assert.Equal("textless", posters[0].Tag);
        Assert.True(posters[0].Pinned);
        Assert.True(posters[0].Selected);
    }

    [Fact]
    public async Task Pinning_refuses_a_tag_the_item_does_not_hold()
    {
        Assert.Equal(PinPosterResult.InvalidTag, await _artwork.PinAsync(_itemId, "nosuchtag", CancellationToken.None));
        Assert.Null(await PinnedTagAsync());
    }

    [Fact]
    public async Task Pinning_refuses_a_backdrop_or_a_logo()
    {
        // Both are real images of this item, so the tag exists — but neither is a poster, and silently
        // accepting one would put a 16:9 backdrop in a 2:3 tile.
        Assert.Equal(PinPosterResult.InvalidTag, await _artwork.PinAsync(_itemId, "backdrop", CancellationToken.None));
        Assert.Equal(PinPosterResult.InvalidTag, await _artwork.PinAsync(_itemId, "logo", CancellationToken.None));
        Assert.Null(await PinnedTagAsync());
    }

    [Fact]
    public async Task Pinning_refuses_a_blank_tag_and_an_unknown_item()
    {
        Assert.Equal(PinPosterResult.InvalidTag, await _artwork.PinAsync(_itemId, "  ", CancellationToken.None));
        Assert.Equal(PinPosterResult.NotFound, await _artwork.PinAsync(Guid.NewGuid(), "english", CancellationToken.None));
    }

    [Fact]
    public async Task Clearing_hands_the_choice_back_to_the_ranking()
    {
        await _artwork.PinAsync(_itemId, "textless", CancellationToken.None);

        Assert.True(await _artwork.ClearAsync(_itemId, CancellationToken.None));

        Assert.Null(await PinnedTagAsync());
        var posters = (await _artwork.ListAsync(_itemId, CancellationToken.None))!
            .Where(image => image.Type == nameof(ImageType.Primary)).ToList();
        Assert.Equal("russian", posters[0].Tag);
    }

    [Fact]
    public async Task Clearing_an_unpinned_item_is_not_an_error_and_clearing_an_unknown_one_is()
    {
        Assert.True(await _artwork.ClearAsync(_itemId, CancellationToken.None));
        Assert.False(await _artwork.ClearAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task A_pin_survives_a_re_enrich_that_adds_more_artwork()
    {
        await _artwork.PinAsync(_itemId, "english", CancellationToken.None);

        using (var context = _db.Create())
        {
            context.ImageAssets.Add(Image(ImageType.Primary, "ru", 0, "newrussian"));
            context.SaveChanges();
        }

        var posters = (await _artwork.ListAsync(_itemId, CancellationToken.None))!
            .Where(image => image.Type == nameof(ImageType.Primary)).ToList();
        // The new Russian poster would win the ranking; the operator's choice still outranks it.
        Assert.Equal("english", posters[0].Tag);
        Assert.True(posters[0].Pinned);
    }

    private async Task<string?> PinnedTagAsync()
    {
        using var context = _db.Create();
        return await context.MediaItems.Where(item => item.Id == _itemId)
            .Select(item => item.PreferredPosterTag)
            .FirstOrDefaultAsync();
    }

    private ImageAsset Image(ImageType type, string? language, int sortOrder, string tag) => new()
    {
        Id = Guid.NewGuid(),
        MediaItemId = _itemId,
        ImageType = type,
        Language = language,
        Provider = "tmdb",
        RemotePath = $"https://image.tmdb.org/{tag}.jpg",
        Tag = tag,
        SortOrder = sortOrder,
    };

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }
}
