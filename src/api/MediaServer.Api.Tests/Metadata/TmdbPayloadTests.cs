using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;

namespace MediaServer.Api.Tests.Metadata;

/// <summary>
/// The shared payload reader — one document, two readers (the library detail page and the preview). These
/// cover what only the preview asks of it: cast, artwork paths, and the kind gating.
/// </summary>
public sealed class TmdbPayloadTests
{
    [Fact]
    public void A_malformed_payload_yields_no_facts_rather_than_throwing()
    {
        Assert.Same(TmdbPayloadFacts.Empty, TmdbPayload.Parse("{ not json", MediaKind.Movie));
        Assert.Same(TmdbPayloadFacts.Empty, TmdbPayload.Parse("[]", MediaKind.Movie));
        Assert.Same(TmdbPayloadFacts.Empty, TmdbPayload.Parse(null, MediaKind.Movie));
    }

    [Fact]
    public void Cast_keeps_the_payload_order_and_stops_at_the_billed_top()
    {
        var members = string.Join(",", Enumerable.Range(1, 30)
            .Select(index => $$"""{ "id": {{index}}, "name": "Actor {{index}}" }"""));
        var facts = TmdbPayload.Parse($$"""{ "credits": { "cast": [{{members}}] } }""", MediaKind.Movie);

        Assert.Equal(20, facts.Cast.Count);
        Assert.Equal("1", facts.Cast[0].ProviderId);
        Assert.Equal("Actor 20", facts.Cast[^1].Name);
    }

    [Fact]
    public void A_nameless_or_idless_credit_is_skipped_rather_than_rendered_blank()
    {
        var facts = TmdbPayload.Parse(
            """{ "credits": { "cast": [{ "id": 1 }, { "name": "No Id" }, { "id": 2, "name": "Real" }] } }""",
            MediaKind.Movie);

        Assert.Equal("Real", Assert.Single(facts.Cast).Name);
    }

    [Fact]
    public void Artwork_paths_become_absolute_urls_and_a_missing_one_stays_null()
    {
        var facts = TmdbPayload.Parse("""{ "poster_path": "/p.jpg", "backdrop_path": null }""", MediaKind.Movie);

        Assert.Equal("https://image.tmdb.org/t/p/original/p.jpg", facts.PosterUrl);
        Assert.Null(facts.BackdropUrl);
    }

    [Fact]
    public void Networks_are_a_series_concept()
    {
        const string raw = """{ "networks": [{ "id": 174, "name": "AMC", "logo_path": "/amc.png" }] }""";

        Assert.Empty(TmdbPayload.Parse(raw, MediaKind.Movie).Networks);
        var network = Assert.Single(TmdbPayload.Parse(raw, MediaKind.Series).Networks);
        Assert.Equal("AMC", network.Name);
        Assert.Equal("https://image.tmdb.org/t/p/original/amc.png", network.LogoUrl);
    }

    [Fact]
    public void The_kind_decides_which_title_and_date_fields_are_read()
    {
        const string raw = """
        { "title": "Film", "name": "Show", "release_date": "2010-07-15", "first_air_date": "2008-01-20" }
        """;
        using var document = JsonDocument.Parse(raw);
        var reference = new ProviderRef("tmdb", "1");

        var movie = TmdbPayload.MapDetails(reference, "en-US", MediaKind.Movie, document.RootElement);
        var series = TmdbPayload.MapDetails(reference, "en-US", MediaKind.Series, document.RootElement);

        Assert.Equal("Film", movie.Title);
        Assert.Equal(2010, movie.ReleaseDate?.Year);
        Assert.Equal("Show", series.Title);
        Assert.Equal(2008, series.ReleaseDate?.Year);
    }
}
