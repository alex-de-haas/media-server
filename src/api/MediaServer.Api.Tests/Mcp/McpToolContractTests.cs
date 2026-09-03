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
    /// <summary>The tools that change something. Everything not here must declare itself read-only.</summary>
    private static readonly string[] WriteTools =
    [
        "set_title_state", "manage_watchlist", "add_torrent", "control_download",
        "match_ingest_item", "advance_ingest_item", "scan_catalog", "refresh_metadata",
    ];

    [Fact]
    public void A_read_tool_says_so_and_a_write_tool_does_not()
    {
        // Both directions matter, for opposite reasons. A read tool missing readOnlyHint is not exported
        // at all — the connector's filter is fail-closed, so forgetting one makes it invisible rather
        // than dangerous, and nothing else would notice. A write tool that claims to be read-only is the
        // reverse: exported, and shown to the operator as safe when it is not.
        var tools = McpToolInvoker.Tools();

        Assert.NotEmpty(tools);
        foreach (var tool in tools)
        {
            var name = tool!["name"]!.GetValue<string>();
            Assert.Equal(!WriteTools.Contains(name), tool["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        }

        // Both classes are non-empty, or the assertion above would hold vacuously for whichever is.
        var names = tools.Select(tool => tool!["name"]!.GetValue<string>()).ToArray();
        Assert.All(WriteTools, write => Assert.Contains(write, names));
        Assert.True(names.Length > WriteTools.Length);
    }

    [Fact]
    public void Nothing_declares_itself_destructive_because_nothing_here_removes_anything()
    {
        // The deletes — a title, a season, an episode, a download with its files — are deliberately
        // absent: irreversible, and this app has no undo, so an agent mistaking one id for another
        // erases the wrong series. If one is ever added this test is what fails, which is the point.
        foreach (var tool in McpToolInvoker.Tools())
        {
            Assert.False(
                tool!["annotations"]!["destructiveHint"]!.GetValue<bool>(),
                $"{tool["name"]} claims to be destructive; the v1 surface holds nothing that removes content");
        }
    }

    [Fact]
    public void Tools_are_named_for_the_question_they_answer()
    {
        var names = McpToolInvoker.Tools().Select(tool => tool!["name"]!.GetValue<string>()).ToArray();

        Assert.Equal(
            [
                "search_library", "get_title", "list_ingest", "get_ingest_item", "list_shelf",
                "list_watch_history", "list_recommendations", "search_ingest_candidates", "search_metadata", "list_downloads",
                "list_catalogs", "get_release_calendar", "preview_release", "set_title_state",
                "manage_watchlist", "add_torrent", "control_download", "match_ingest_item",
                "advance_ingest_item", "scan_catalog", "refresh_metadata", "get_server_status",
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
    public void A_detached_operation_says_it_was_accepted_rather_than_done()
    {
        // Scan, refresh and a torrent add all return before the work does. A description promising the
        // outcome would have the operator believe a film is downloaded when it is queued — they find out
        // when it is not there.
        var tools = McpToolInvoker.Tools().ToDictionary(
            tool => tool!["name"]!.GetValue<string>(), tool => tool!["description"]!.GetValue<string>());

        foreach (var name in new[] { "add_torrent", "scan_catalog", "refresh_metadata" })
        {
            Assert.Contains("accepted", tools[name], StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_match_tool_says_to_read_the_item_first()
    {
        // The source file ids a match names come from get_ingest_item. A model that guesses them gets a
        // FileNotFound outcome, which reads like a broken tool rather than a missing step.
        var match = McpToolInvoker.Tools()
            .Single(tool => tool!["name"]!.GetValue<string>() == "match_ingest_item")!;

        Assert.Contains("get_ingest_item", match["description"]!.GetValue<string>(), StringComparison.Ordinal);
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
