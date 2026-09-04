using System.Text.Json.Nodes;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Jobs;
using MediaServer.Api.Library;
using MediaServer.Api.Mcp;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Metadata;
using MediaServer.Api.Realtime;
using MediaServer.Api.Torrents;
using MediaServer.Api.WatchHistory;
using MediaServer.Api.Tests.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Mcp;

/// <summary>
/// The tools in use. Most of what is asserted here is about answers that would otherwise be
/// confidently wrong: a "no" that came from an unread catalog, a window that hides the rest, a filter
/// silently dropped, and watched state answered for nobody in particular.
/// </summary>
public sealed class McpToolInvokerTests : IDisposable
{
    private readonly PipelineTestHarness _harness = new();
    private readonly IServiceScope _scope;
    private readonly MediaServerDbContext _database;
    private readonly CatalogScanQueue _queue = new();
    private readonly McpToolInvoker _invoker;

    public McpToolInvokerTests()
    {
        _scope = _harness.CreateScope();
        _database = _scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        _invoker = new McpToolInvoker(
            _database,
            new LibraryReadService(
                _database,
                new UserDataService(_database, TimeProvider.System),
                new MediaServerSettings { SupportedLanguages = ["en-US"] }),
            _scope.ServiceProvider.GetRequiredService<IngestService>(),
            new CatalogScanCoordinator(
                _database, new JobService(_database, new NullRealtimeNotifier()), _queue),
            _scope.ServiceProvider.GetRequiredService<CatalogService>(),
            // Recommendations and the watchlist stay null: thin projections over services with their
            // own tests, whose dependency trees would be scaffolding here. Torrents is real, because
            // how this tool reports a *refusal* is the thing under test.
            _scope.ServiceProvider.GetRequiredService<TorrentService>(),
            recommendations: null!,
            watchlist: null!,
            _scope.ServiceProvider.GetRequiredService<WatchHistoryCalendarService>(),
            _scope.ServiceProvider.GetRequiredService<IMetadataProvider>(),
            new UserDataService(_database, TimeProvider.System),
            new CatalogRefreshCoordinator(
                _database, new JobService(_database, new NullRealtimeNotifier()), new CatalogRefreshQueue()));
    }

    [Fact]
    public async Task An_unknown_tool_fails_as_a_result_rather_than_ending_the_turn()
    {
        // isError on a normal result is the protocol's own signal: the model reads why and picks
        // something else. A JSON-RPC error would end the turn instead.
        var result = await CallAsync("drop_everything", new JsonObject());

        Assert.True(result["result"]!["isError"]!.GetValue<bool>());
        Assert.Null(result["error"]);
    }

    [Fact]
    public async Task An_unknown_filter_value_is_refused_rather_than_ignored()
    {
        // The worst available outcome: the call succeeds, the filter does not apply, and "nothing is
        // failing" comes back as a list of everything.
        var result = await CallAsync("list_ingest", new JsonObject { ["status"] = "Broken" });

        Assert.True(result["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("NeedsReview", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watched_state_is_refused_when_the_call_carries_no_user()
    {
        // Answering for "nobody" reports every title as unwatched, which reads as a fact about the
        // library rather than about the missing caller.
        var result = await CallAsync("search_library", new JsonObject { ["watched"] = false }, appUserId: null);

        Assert.True(result["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Hosty user", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_result_says_when_it_is_about_an_unread_catalog_and_not_about_the_library()
    {
        // The failure this exists to stop: an agent answering "you don't have that film" when the truth
        // is that nothing has looked. Paired with a scanned catalog, where no such note belongs — a note
        // attached to every empty answer would train the model to skip it.
        await _harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Anything.2021", "Anything.2021/movie.mkv");

        var unscanned = Payload(await CallAsync("search_library", new JsonObject { ["query"] = "nothing" }));
        Assert.Empty((JsonArray)unscanned["titles"]!);
        Assert.Contains("never been scanned", unscanned["note"]!.GetValue<string>(), StringComparison.Ordinal);

        MarkScanned();
        var scanned = Payload(await CallAsync("search_library", new JsonObject { ["query"] = "nothing" }));
        Assert.Null(scanned["note"]);
    }

    [Fact]
    public async Task A_windowed_list_says_how_much_it_left_behind()
    {
        Guid? catalogId = null;
        for (var i = 0; i < 4; i++)
        {
            var seeded = await _harness.SeedCompletedDownloadAsync(
                CatalogType.Movie, $"Release.{i}.2021", $"Release.{i}.2021/movie.mkv", catalogId);
            catalogId = seeded.CatalogId;
        }

        var page = Payload(await CallAsync("list_ingest", new JsonObject { ["limit"] = 2 }));
        var window = page["window"]!;

        Assert.Equal(2, window["returned"]!.GetValue<int>());
        Assert.Equal(4, window["total"]!.GetValue<int>());
        Assert.True(window["truncated"]!.GetValue<bool>());

        // Paired: the last page of the same list is not truncated, so the flag means something.
        var last = Payload(await CallAsync("list_ingest", new JsonObject { ["limit"] = 2, ["offset"] = 2 }));
        Assert.False(last["window"]!["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_status_tool_reports_a_catalog_nothing_has_scanned()
    {
        await _harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Anything.2021", "Anything.2021/movie.mkv");

        var status = Payload(await CallAsync("get_server_status", new JsonObject()));

        var catalog = Assert.Single((JsonArray)status["catalogs"]!)!;
        Assert.True(catalog["neverScanned"]!.GetValue<bool>());
        Assert.False(catalog["scanning"]!.GetValue<bool>());
        Assert.Equal(1, status["pipeline"]!["Pending"]!.GetValue<int>());
    }

    [Fact]
    public async Task Setting_no_field_at_all_is_refused_rather_than_treated_as_clearing_them()
    {
        // Nothing asked for is not the same as everything cleared. Writing defaults for the fields the
        // caller never mentioned would wipe a rating nobody touched.
        var result = await CallAsync("set_title_state", new JsonObject { ["id"] = Guid.NewGuid().ToString() });

        Assert.True(result["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("at least one", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Personal_state_cannot_be_written_without_a_user()
    {
        var result = await CallAsync(
            "set_title_state",
            new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["watched"] = true },
            appUserId: null);

        Assert.True(result["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Hosty user", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_queued_scan_answers_accepted_and_a_second_one_says_it_is_already_running()
    {
        // The contract detached work is held to. Reporting an enqueue as a finished scan is a lie the
        // operator only discovers when the library still looks the same.
        await _harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Anything.2021", "Anything.2021/movie.mkv");
        var catalogId = _database.Catalogs.Select(catalog => catalog.Id).Single();

        var first = Payload(await CallAsync("scan_catalog", new JsonObject { ["catalogId"] = catalogId.ToString() }));
        Assert.Equal("accepted", first["outcome"]!.GetValue<string>());
        Assert.NotNull(first["jobId"]);

        // The reservation belongs to the scan, not to the queue that admits requests — which is what
        // makes it visible to the synchronous route and the nightly job too. Held here the way a running
        // scan holds it.
        _queue.TryReserve(catalogId);
        var second = Payload(await CallAsync("scan_catalog", new JsonObject { ["catalogId"] = catalogId.ToString() }));
        Assert.Equal("already-running", second["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unknown_catalog_is_reported_as_such_rather_than_accepted()
    {
        // "Accepted" for a catalog that does not exist would have the operator waiting for a scan that
        // was never going to happen.
        var payload = Payload(await CallAsync(
            "scan_catalog", new JsonObject { ["catalogId"] = Guid.NewGuid().ToString() }));

        Assert.Equal("not-found", payload["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_match_without_a_source_file_is_refused_before_it_reaches_the_pipeline()
    {
        // The ids come from get_ingest_item, and a model that guesses produces a FileNotFound outcome
        // that reads like a broken tool. Refusing here says which step was skipped.
        var (ingestId, _, _) = await _harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Ambiguous.2021", "Ambiguous.2021/movie.mkv");

        var result = await CallAsync("match_ingest_item", new JsonObject
        {
            ["id"] = ingestId.ToString(),
            ["groups"] = new JsonArray(new JsonObject
            {
                ["provider"] = "tmdb", ["providerId"] = "27205", ["kind"] = "movie", ["title"] = "Inception",
                ["files"] = new JsonArray(new JsonObject()),
            }),
        });

        Assert.True(result["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("sourceFileId", Text(result), StringComparison.Ordinal);
    }

    /// <summary>Stamps the catalogs the way a finished scan does, whichever entry point ran it.</summary>
    [Fact]
    public async Task A_maintenance_tool_is_refused_for_a_user_who_is_not_an_administrator()
    {
        // The endpoint asks only for an authenticated user, which is right for reading a library and
        // wrong for maintenance: the catalog routes these call are admin-only, and reaching the
        // coordinators in-process would otherwise walk around that check entirely.
        await _harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Anything.2021", "Anything.2021/movie.mkv");
        var catalogId = _database.Catalogs.Select(catalog => catalog.Id).Single();
        // A fresh argument object per call: a JsonNode cannot be re-parented, so reusing one turns the
        // second call into an exception rather than the assertion it was written to make.
        JsonObject Arguments() => new() { ["catalogId"] = catalogId.ToString() };

        var refused = await CallAsync("scan_catalog", Arguments(), isAdministrator: false);
        Assert.True(refused["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("administrator", Text(refused), StringComparison.OrdinalIgnoreCase);

        // Beside the same call as an administrator, or a gate that refused everyone would pass too.
        Assert.Equal("accepted", Payload(await CallAsync("scan_catalog", Arguments()))["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reading_the_library_is_not_an_administrator_action()
    {
        // The gate must be a list, not a mood. An ordinary host user asking what is in the library is
        // the ordinary case, and refusing it would make the surface useless to everyone but admins.
        var payload = Payload(await CallAsync(
            "search_library", new JsonObject { ["query"] = "anything" }, isAdministrator: false));

        Assert.Empty((JsonArray)payload["titles"]!);
    }

    [Fact]
    public async Task An_episode_match_through_the_tool_creates_episodes_and_not_a_film()
    {
        // The pipeline branches on MediaKind.Episode and sends every other kind through movie
        // resolution, so a tool offering only 'movie' and 'series' cannot repair an episode ingest at
        // all: 'series' resolves each episode file as a film, which succeeds and is wrong. The provider
        // id on an episode group is the *series* id, which is why this pairing needs a test rather than
        // a description.
        var (ingestId, _, _) = await _harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "Obscure.Show.S01",
            "Obscure.Show.S01/Obscure.Show.S01E01.mkv",
            additionalSourceRelativePaths: ["Obscure.Show.S01/Obscure.Show.S01E02.mkv"]);
        await _harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        var files = await _database.SourceFiles.OrderBy(file => file.RelativePath).ToListAsync();
        var result = await CallAsync("match_ingest_item", new JsonObject
        {
            ["id"] = ingestId.ToString(),
            ["groups"] = new JsonArray(new JsonObject
            {
                ["provider"] = "tmdb",
                ["providerId"] = "4242",
                ["kind"] = "episode",
                ["title"] = "Obscure Show",
                ["year"] = 2020,
                ["files"] = new JsonArray(
                    new JsonObject { ["sourceFileId"] = files[0].Id.ToString(), ["season"] = 1, ["episode"] = 1 },
                    new JsonObject { ["sourceFileId"] = files[1].Id.ToString(), ["season"] = 1, ["episode"] = 2 }),
            }),
        });

        Assert.Equal("accepted", Payload(result)["outcome"]!.GetValue<string>());
        await _harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = _harness.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var episodes = await verify.MediaItems.Where(item => item.Kind == MediaKind.Episode)
            .OrderBy(item => item.IndexNumber).ToListAsync();
        Assert.Equal([1, 2], episodes.Select(episode => episode.IndexNumber ?? 0));
        Assert.Empty(await verify.MediaItems.Where(item => item.Kind == MediaKind.Movie).ToListAsync());
    }

    private void MarkScanned()
    {
        foreach (var catalog in _database.Catalogs.ToList())
        {
            catalog.LastScannedAt = DateTimeOffset.UtcNow;
        }

        _database.SaveChanges();
    }

    [Fact]
    public async Task A_malformed_history_date_is_refused_rather_than_quietly_replaced()
    {
        // The failure this prevents is a confident wrong answer: an unreadable `from` used to fall back
        // to the default thirty days, so a typo produced a real-looking report about a period nobody
        // asked about, with nothing in the reply to say which period it was.
        var result = await CallAsync("list_watch_history", new JsonObject { ["from"] = "2026-13-01" });

        Assert.True(result["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains(
            "YYYY-MM-DD",
            result["result"]!["content"]![0]!["text"]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_omitted_history_date_still_uses_the_default_window()
    {
        // Paired with the case above, or "refuse anything unreadable" could have been implemented as
        // "refuse anything absent" and both would look correct from one side.
        var result = await CallAsync("list_watch_history", new JsonObject());

        Assert.Null(result["result"]!["isError"]);
    }

    [Fact]
    public async Task Watch_history_is_refused_when_the_call_carries_no_user()
    {
        var result = await CallAsync("list_watch_history", new JsonObject(), appUserId: null);

        Assert.True(result["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_torrent_the_service_rejects_is_a_tool_error_and_not_a_crash()
    {
        // `TorrentRequestException` derives from `Exception`, and the invoker caught only
        // `FormatException` and `InvalidOperationException` — so every way this service says no
        // escaped as a 500 and ended the caller's turn. Accepting `.torrent` files widened that:
        // "not enough free space", the refusal this feature exists to deliver *earlier*, was among
        // the answers being lost.
        var result = await CallAsync("add_torrent", new JsonObject
        {
            ["catalogId"] = Guid.NewGuid().ToString(),
            ["torrentFileBase64"] = "bm90LWEtdG9ycmVudA==",
        });

        Assert.True(result["result"]!["isError"]!.GetValue<bool>());
        Assert.Null(result["error"]);
        // The service's own words reach the model, so it can tell "catalog is gone" from "that is
        // not a torrent" instead of retrying the same call.
        Assert.NotEmpty(result["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Add_torrent_refuses_both_sources_and_refuses_neither()
    {
        // The service enforces this too, first thing in `AddAsync`. Refusing here as well is about
        // the wording: the model is told which of *its* two arguments to drop, in the names the
        // schema gave it, rather than the service's phrasing of the same rule.
        var both = await CallAsync("add_torrent", new JsonObject
        {
            ["catalogId"] = Guid.NewGuid().ToString(),
            ["magnet"] = "magnet:?xt=urn:btih:abc",
            ["torrentFileBase64"] = "ZA==",
        });
        Assert.True(both["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("exactly one", both["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        var neither = await CallAsync("add_torrent", new JsonObject
        {
            ["catalogId"] = Guid.NewGuid().ToString(),
        });
        Assert.True(neither["result"]!["isError"]!.GetValue<bool>());
    }

    private async Task<JsonNode> CallAsync(
        string tool, JsonObject arguments, int? appUserId = 1, bool isAdministrator = true)
    {
        var parameters = new JsonObject { ["name"] = tool, ["arguments"] = arguments };
        var result = await _invoker.CallAsync(
            JsonValue.Create(1), parameters, appUserId, isAdministrator, CancellationToken.None);
        return JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Unwrap(result)))!;
    }

    private static object Unwrap(object result)
        => result.GetType().GetProperty("Value")?.GetValue(result) ?? result;

    /// <summary>The tool's payload, which MCP carries as a JSON string inside the text content.</summary>
    private static JsonObject Payload(JsonNode result) =>
        (JsonObject)JsonNode.Parse(Text(result))!;

    private static string Text(JsonNode result) =>
        result["result"]!["content"]![0]!["text"]!.GetValue<string>();

    public void Dispose()
    {
        _scope.Dispose();
        _harness.Dispose();
    }
}
