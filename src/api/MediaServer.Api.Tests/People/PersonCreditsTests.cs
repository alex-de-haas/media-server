using MediaServer.Api.Data;
using MediaServer.Api.People;

namespace MediaServer.Api.Tests.People;

/// <summary>
/// Pure mapping of a cached TMDb detail payload's <c>credits</c> into <see cref="PersonCredit"/> rows:
/// cast carries character, crew carries job/department, ids and names are required, and exact duplicates
/// collapse.
/// </summary>
public sealed class PersonCreditsTests
{
    // A movie payload with two cast (one with null profile and no character) and two crew (a director and
    // a writer who is the same person as the lead actor — i.e. cast + crew for one provider id).
    private const string MovieCredits = """
        {
          "id": 27205,
          "title": "Inception",
          "credits": {
            "cast": [
              { "id": 6193, "name": "Leonardo DiCaprio", "character": "Cobb", "profile_path": "/leo.jpg", "order": 0, "known_for_department": "Acting" },
              { "id": 24045, "name": "Joseph Gordon-Levitt", "character": "", "profile_path": null, "order": 1 }
            ],
            "crew": [
              { "id": 525, "name": "Christopher Nolan", "job": "Director", "department": "Directing", "profile_path": "/nolan.jpg", "known_for_department": "Directing" },
              { "id": 6193, "name": "Leonardo DiCaprio", "job": "Producer", "department": "Production", "profile_path": "/leo.jpg" }
            ]
          }
        }
        """;

    [Fact]
    public void Parse_maps_cast_with_character_and_order()
    {
        var credits = PersonCredits.Parse(MovieCredits);

        var cobb = credits.Single(credit => credit.Role == PersonRole.Cast && credit.ProviderId == "6193");
        Assert.Equal("Leonardo DiCaprio", cobb.Name);
        Assert.Equal("Cobb", cobb.Character);
        Assert.Equal("/leo.jpg", cobb.ProfilePath);
        Assert.Equal("Acting", cobb.KnownForDepartment);
        Assert.Equal(0, cobb.Order);
        Assert.Null(cobb.Job);
        Assert.Null(cobb.Department);
    }

    [Fact]
    public void Parse_maps_crew_with_job_and_department()
    {
        var credits = PersonCredits.Parse(MovieCredits);

        var nolan = credits.Single(credit => credit.Role == PersonRole.Crew && credit.ProviderId == "525");
        Assert.Equal("Director", nolan.Job);
        Assert.Equal("Directing", nolan.Department);
        Assert.Null(nolan.Character);
    }

    [Fact]
    public void Parse_treats_empty_character_and_missing_profile_as_null()
    {
        var credits = PersonCredits.Parse(MovieCredits);

        var jgl = credits.Single(credit => credit.Role == PersonRole.Cast && credit.ProviderId == "24045");
        Assert.Null(jgl.Character);
        Assert.Null(jgl.ProfilePath);
        Assert.Null(jgl.KnownForDepartment);
    }

    [Fact]
    public void Parse_keeps_a_person_who_is_both_cast_and_crew_as_two_credits()
    {
        var credits = PersonCredits.Parse(MovieCredits);

        var leo = credits.Where(credit => credit.ProviderId == "6193").ToList();
        Assert.Equal(2, leo.Count);
        Assert.Contains(leo, credit => credit.Role == PersonRole.Cast && credit.Character == "Cobb");
        Assert.Contains(leo, credit => credit.Role == PersonRole.Crew && credit.Job == "Producer");
    }

    [Fact]
    public void Parse_skips_entries_missing_an_id_or_name()
    {
        const string raw = """
            {
              "credits": {
                "cast": [
                  { "name": "No Id", "character": "Ghost" },
                  { "id": 1, "character": "Anonymous" },
                  { "id": 2, "name": "Real Actor", "character": "Hero" }
                ],
                "crew": []
              }
            }
            """;

        var credits = PersonCredits.Parse(raw);

        Assert.Equal("2", Assert.Single(credits).ProviderId);
    }

    [Fact]
    public void Parse_collapses_exact_duplicate_credits()
    {
        const string raw = """
            {
              "credits": {
                "cast": [],
                "crew": [
                  { "id": 525, "name": "Christopher Nolan", "job": "Director", "department": "Directing" },
                  { "id": 525, "name": "Christopher Nolan", "job": "Director", "department": "Directing" }
                ]
              }
            }
            """;

        var credits = PersonCredits.Parse(raw);

        Assert.Single(credits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{ \"title\": \"No credits object\" }")]
    public void Parse_returns_empty_for_blank_or_creditless_payloads(string? raw)
    {
        Assert.Empty(PersonCredits.Parse(raw));
    }

    [Fact]
    public void ProfileUrl_builds_absolute_url_or_null()
    {
        Assert.Equal("https://image.tmdb.org/t/p/original/leo.jpg", PersonCredits.ProfileUrl("/leo.jpg"));
        Assert.Null(PersonCredits.ProfileUrl(null));
        Assert.Null(PersonCredits.ProfileUrl("  "));
    }
}
