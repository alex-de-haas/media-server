using System.Text.Json.Nodes;
using MediaServer.Api.Mcp;

namespace MediaServer.Api.Tests.Mcp;

/// <summary>
/// What the tool declarations promise, before any of them is called.
/// </summary>
/// <remarks>
/// Cheap to assert and expensive to get wrong: a missing annotation does not degrade the surface, it
/// removes it, and a description that does not say when to reach for a tool leaves the model choosing
/// by name alone.
/// </remarks>
public sealed class McpToolContractTests
{
    [Fact]
    public void Every_tool_declares_itself_read_only()
    {
        // The Hosty connector's filter is fail-closed: a tool with no readOnlyHint is treated as
        // possibly mutating and is not exported at all. Forgetting one makes it invisible rather than
        // dangerous, which is why nothing else would notice.
        var tools = McpToolInvoker.Tools();

        Assert.NotEmpty(tools);
        foreach (var tool in tools)
        {
            Assert.True(
                tool!["annotations"]!["readOnlyHint"]!.GetValue<bool>(),
                $"{tool["name"]} must declare readOnlyHint");
        }
    }

    [Fact]
    public void Tools_are_named_for_the_question_they_answer()
    {
        var names = McpToolInvoker.Tools().Select(tool => tool!["name"]!.GetValue<string>()).ToArray();

        Assert.Equal(
            [
                "search_library", "get_title", "list_ingest", "get_ingest_item", "list_shelf",
                "list_recommendations", "search_ingest_candidates", "search_metadata", "list_downloads",
                "list_catalogs", "get_release_calendar", "preview_release", "get_server_status",
            ],
            names);
    }

    [Fact]
    public void The_pipeline_tool_says_what_it_is_for_because_nothing_else_surfaces_a_stalled_title()
    {
        // A model that does not know NeedsReview exists will answer "it is not in your library" and stop,
        // which is true and useless — the item is downloaded and waiting for a person.
        var listIngest = McpToolInvoker.Tools()
            .Single(tool => tool!["name"]!.GetValue<string>() == "list_ingest")!;

        Assert.Contains("NeedsReview", listIngest["description"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_recommendation_tool_says_to_prefer_it_over_ranking_by_hand()
    {
        // A model that ranks search results itself will be slower, worse, and inconsistent with what the
        // web UI shows — the engine already knows what was watched, hidden, and how the operator weighs
        // popularity. Saying so in the description is the only place that judgement can live.
        var tool = McpToolInvoker.Tools()
            .Single(entry => entry!["name"]!.GetValue<string>() == "list_recommendations")!;

        Assert.Contains("rather than ranking", tool["description"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_release_tools_say_which_one_answers_for_an_untracked_title()
    {
        // They look interchangeable and are not: the calendar reads the watchlist and answers nothing for
        // a title nobody tracks, which is the case the question is usually about.
        var tools = McpToolInvoker.Tools().ToDictionary(
            tool => tool!["name"]!.GetValue<string>(), tool => tool!["description"]!.GetValue<string>());

        Assert.Contains("preview_release", tools["get_release_calendar"], StringComparison.Ordinal);
        Assert.Contains("nobody is tracking", tools["preview_release"], StringComparison.Ordinal);
    }

    [Fact]
    public void Every_tool_refuses_arguments_it_does_not_declare()
    {
        // additionalProperties:false is what turns a misspelled filter into an error the model can see.
        // Accepted and ignored, a stray argument reads as "the filter applied and matched everything".
        foreach (var tool in McpToolInvoker.Tools())
        {
            Assert.False(
                tool!["inputSchema"]!["additionalProperties"]!.GetValue<bool>(),
                $"{tool["name"]} must refuse undeclared arguments");
        }
    }
}
