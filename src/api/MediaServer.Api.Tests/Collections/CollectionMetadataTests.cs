using MediaServer.Api.Collections;

namespace MediaServer.Api.Tests.Collections;

/// <summary>
/// Parsing of the <c>belongs_to_collection</c> object from a cached TMDb movie payload: the id renders as a
/// string, name and artwork paths are read, and absent/blank/invalid input yields null (most movies have no
/// collection at all).
/// </summary>
public sealed class CollectionMetadataTests
{
    [Fact]
    public void Parse_reads_the_collection_identity_name_and_artwork()
    {
        const string raw = """
            { "title": "The Fellowship of the Ring",
              "belongs_to_collection": { "id": 119, "name": "The Lord of the Rings Collection", "poster_path": "/p.jpg", "backdrop_path": "/b.jpg" } }
            """;

        var info = CollectionMetadata.Parse(raw);

        Assert.NotNull(info);
        Assert.Equal("119", info!.ProviderId);
        Assert.Equal("The Lord of the Rings Collection", info.Name);
        Assert.Equal("/p.jpg", info.PosterPath);
        Assert.Equal("/b.jpg", info.BackdropPath);
    }

    [Fact]
    public void Parse_nulls_blank_artwork_paths()
    {
        const string raw = """
            { "belongs_to_collection": { "id": 10, "name": "Star Wars Collection", "poster_path": null, "backdrop_path": "" } }
            """;

        var info = CollectionMetadata.Parse(raw);

        Assert.NotNull(info);
        Assert.Null(info!.PosterPath);
        Assert.Null(info.BackdropPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "belongs_to_collection": null }""")]
    [InlineData("""{ "belongs_to_collection": { "id": 1 } }""")]   // no name
    [InlineData("""{ "belongs_to_collection": { "name": "Nameless" } }""")] // no id
    public void Parse_returns_null_when_there_is_no_usable_collection(string? raw)
    {
        Assert.Null(CollectionMetadata.Parse(raw));
    }

    [Fact]
    public void ImageUrl_prefixes_the_cdn_base_and_passes_through_null()
    {
        Assert.Equal("https://image.tmdb.org/t/p/original/p.jpg", CollectionMetadata.ImageUrl("/p.jpg"));
        Assert.Null(CollectionMetadata.ImageUrl(null));
        Assert.Null(CollectionMetadata.ImageUrl("  "));
    }
}
