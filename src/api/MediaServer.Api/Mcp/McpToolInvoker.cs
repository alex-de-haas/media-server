using System.Text.Json.Nodes;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using static MediaServer.Api.Mcp.McpProtocol;

namespace MediaServer.Api.Mcp;

/// <summary>Declares the tools and runs them against this server's own services.</summary>
public sealed class McpToolInvoker(
    MediaServerDbContext database,
    LibraryReadService library,
    IngestService ingest,
    CatalogScanCoordinator scans)
{
    public static JsonArray Tools() =>
    [
        Tool(
            "search_library",
            "Searches the titles this server holds. Use it to answer 'do I have this?' and to narrow a "
            + "suggestion: rows carry genres, runtime and community rating, so 'an unwatched comedy "
            + "under two hours' needs no further calls. `about` matches the synopsis and the provider's "
            + "keywords, which is how a question like 'something about a plane hijacking' is answered; "
            + "`genres` must all match, so two genres mean a title that is both.",
            new JsonObject
            {
                ["query"] = Prop("string", "Substring of the title, in any language it is known by."),
                ["about"] = Prop("string", "What the title is about — matched against synopsis and keywords."),
                ["genres"] = Prop("string", "Comma-separated genres. All must match."),
                ["kind"] = Prop("string", "Restrict to 'movie' or 'series'."),
                ["watched"] = Prop("boolean", "True for watched titles only, false for unwatched only."),
                ["limit"] = Prop("integer", "Maximum rows. Capped at 200; the default is 25."),
                ["offset"] = Prop("integer", "Rows to skip, for paging."),
            }),
        Tool(
            "get_title",
            "One title in summary: identity, year, genres, runtime, ratings, watched state, season and "
            + "episode counts, and its files counted and sized. Not the synopsis, artwork or per-file "
            + "detail — a model cannot see a poster and a long series would spend the whole context on "
            + "links. Pass verbose for the synopsis and the file list.",
            new JsonObject
            {
                ["id"] = Prop("string", "The library item id, as returned by search_library."),
                ["verbose"] = Prop("boolean", "Include the synopsis and each source file."),
            },
            "id"),
        Tool(
            "list_ingest",
            "The download-and-identify pipeline: what is running, what failed, and what is waiting for a "
            + "person. Ask this when a title was downloaded but never appeared — status 'NeedsReview' is "
            + "an item the operator has to identify, and nothing else will surface it. The title filter "
            + "matches the identified title, the pinned target, and the release name, so it finds items "
            + "that have no identity yet.",
            new JsonObject
            {
                ["status"] = Prop("string", "Pending, Running, NeedsReview, Failed or Done."),
                ["stage"] = Prop("string", "Download, Identify, Organize, Probe, Enrich or Publish."),
                ["title"] = Prop("string", "Substring of the title or the release name."),
                ["limit"] = Prop("integer", "Maximum rows. Capped at 500; the default is 50."),
                ["offset"] = Prop("integer", "Rows to skip, for paging."),
            }),
        Tool(
            "get_ingest_item",
            "One pipeline item in full: its stage history, what it is waiting on, the error that stopped "
            + "it, and the source files a match would have to name. Read this before repairing an "
            + "identification — the file ids a match refers to come from here.",
            new JsonObject { ["id"] = Prop("string", "The ingest item id, as returned by list_ingest.") },
            "id"),
        Tool(
            "get_server_status",
            "What this server is doing and whether it can answer for itself: catalogs and whether each "
            + "has ever been scanned, scans running now, and pipeline items per status. Check it before "
            + "concluding a title is absent — a catalog nothing has scanned holds nothing this server "
            + "knows about, which is not the same as holding nothing.",
            []),
    ];

    public async Task<IResult> CallAsync(
        JsonNode? id, JsonNode? parameters, int? appUserId, CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var arguments = parameters?["arguments"];

        try
        {
            return name switch
            {
                "search_library" => Content(id, await SearchLibraryAsync(arguments, appUserId, cancellationToken)),
                "get_title" => await GetTitleAsync(id, arguments, appUserId, cancellationToken),
                "list_ingest" => Content(id, await ListIngestAsync(arguments, cancellationToken)),
                "get_ingest_item" => await GetIngestItemAsync(id, arguments, cancellationToken),
                "get_server_status" => Content(id, await ServerStatusAsync(cancellationToken)),
                _ => Failure(id, $"Unknown tool: {name}"),
            };
        }
        catch (McpRefusedException refusal)
        {
            // Passed through unchanged: this is a refusal about the request, not about its syntax.
            return Failure(id, refusal.Message);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            // The model's own arguments were wrong. Reported as a tool failure so it can correct them.
            return Failure(id, $"Those arguments could not be read: {exception.Message}");
        }
    }

    private async Task<JsonObject> SearchLibraryAsync(
        JsonNode? arguments, int? appUserId, CancellationToken cancellationToken)
    {
        var watched = Bool(arguments, "watched");
        if (watched is not null && appUserId is null)
        {
            // Watched state is per person. Answering for "nobody" would report every title as unwatched,
            // which reads as a fact about the library rather than about the missing caller.
            throw new McpRefusedException(
                "Filtering by watched state needs a Hosty user, and this call carried none.");
        }

        var limit = Int(arguments, "limit");
        var offset = Int(arguments, "offset");
        var page = await library.SearchAsync(
            new LibrarySearchQuery(
                Kind: ParseKind(Str(arguments, "kind")),
                Title: Str(arguments, "query"),
                Watched: watched,
                About: Str(arguments, "about"),
                Genres: Csv(arguments, "genres"),
                Limit: limit,
                Offset: offset),
            appUserId,
            cancellationToken);

        var rows = new JsonArray();
        foreach (var item in page.Items)
        {
            rows.Add(new JsonObject
            {
                ["id"] = item.Id,
                ["kind"] = item.Kind,
                ["title"] = item.Title,
                ["year"] = item.Year,
                ["genres"] = new JsonArray([.. (item.Genres ?? []).Select(genre => (JsonNode)genre!)]),
                ["runtimeMinutes"] = item.RuntimeTicks is { } ticks ? (int?)TimeSpan.FromTicks(ticks).TotalMinutes : null,
                ["communityRating"] = item.CommunityRating,
                ["watched"] = item.UserData?.Played,
            });
        }

        var payload = WithWindow(new JsonObject { ["titles"] = rows }, rows.Count, page.Total, page.Limit, page.Offset);
        return WithNote(payload, page.Total == 0 ? await EmptyLibraryNoteAsync(cancellationToken) : null);
    }

    /// <summary>Says whether "nothing matched" is about the library or about what has been read.</summary>
    private async Task<string?> EmptyLibraryNoteAsync(CancellationToken cancellationToken)
    {
        var state = await scans.ListStateAsync(cancellationToken);
        if (state.Count == 0)
        {
            return "This server has no catalogs configured, so it holds nothing yet — the search matched "
                + "nothing because there is nothing to match, not because the title is absent.";
        }

        var unscanned = state.Count(entry => entry.NeverScanned);
        var scanning = state.Count(entry => entry.Scanning);
        if (unscanned == 0 && scanning == 0)
        {
            return null;
        }

        return $"No match — but {unscanned} of {state.Count} catalog(s) have never been scanned"
            + (scanning > 0 ? $" and {scanning} are being scanned now" : string.Empty)
            + ". A title in an unscanned catalog is on disk and unknown to this server, so this is not "
            + "yet an answer about whether you have it.";
    }

    private async Task<IResult> GetTitleAsync(
        JsonNode? id, JsonNode? arguments, int? appUserId, CancellationToken cancellationToken)
    {
        var itemId = Id(arguments, "id")
            ?? throw new InvalidOperationException("id must be a library item id.");
        var detail = await library.GetDetailAsync(itemId, appUserId, cancellationToken);
        if (detail is null)
        {
            return Failure(id, "No title with that id. Ids come from search_library.");
        }

        var verbose = Bool(arguments, "verbose") ?? false;
        var payload = new JsonObject
        {
            ["id"] = detail.Id,
            ["kind"] = detail.Kind,
            ["title"] = detail.Title,
            ["originalTitle"] = detail.OriginalTitle,
            ["year"] = detail.Year,
            ["genres"] = new JsonArray([.. detail.Genres.Select(genre => (JsonNode)genre!)]),
            ["runtimeMinutes"] = detail.RuntimeTicks is { } ticks ? (int?)TimeSpan.FromTicks(ticks).TotalMinutes : null,
            ["officialRating"] = detail.OfficialRating,
            ["communityRating"] = detail.CommunityRating,
            ["status"] = detail.Status,
            ["watched"] = detail.UserData?.Played,
            ["favorite"] = detail.UserData?.IsFavorite,
                        // `UserRating`, not `Rating`: the DTO is shared with the Jellyfin surface, where `Rating` is
            // a 0-10 double, and emitting a 4-star verdict under that name would claim "four out of ten".
            ["userRating"] = detail.UserData?.UserRating,
            ["seasonsHeld"] = detail.Seasons?.Count,
            // Counted and sized rather than listed: a series with nine seasons has hundreds of files and
            // the question this answers is "how much of it do I have", not "which bytes".
            ["sourceCount"] = detail.MediaSources.Count,
            ["sourceBytes"] = detail.MediaSources.Sum(source => source.SizeBytes),
        };

        if (verbose)
        {
            payload["overview"] = detail.Overview;
            payload["sources"] = new JsonArray([.. detail.MediaSources.Select(source => (JsonNode)new JsonObject
            {
                ["id"] = source.Id,
                ["fileName"] = source.FileName,
                ["bytes"] = source.SizeBytes,
            })]);
        }

        return Content(id, payload);
    }

    private async Task<JsonObject> ListIngestAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        var page = await ingest.ListAsync(
            new IngestListQuery(
                Status: ParseEnum<IngestStatus>(Str(arguments, "status"), "status"),
                Stage: ParseEnum<IngestStage>(Str(arguments, "stage"), "stage"),
                Title: Str(arguments, "title"),
                Limit: Int(arguments, "limit"),
                Offset: Int(arguments, "offset")),
            cancellationToken);

        var rows = new JsonArray();
        foreach (var item in page.Items)
        {
            rows.Add(new JsonObject
            {
                ["id"] = item.Id,
                ["title"] = item.MediaTitle ?? item.TargetTitle ?? item.DownloadName,
                ["identified"] = item.MediaItemId is not null,
                ["stage"] = item.Stage,
                ["status"] = item.Status,
                ["lastError"] = item.LastError,
                ["downloadId"] = item.DownloadId,
            });
        }

        return WithWindow(new JsonObject { ["items"] = rows }, rows.Count, page.Total, page.Limit, page.Offset);
    }

    private async Task<IResult> GetIngestItemAsync(
        JsonNode? id, JsonNode? arguments, CancellationToken cancellationToken)
    {
        var itemId = Id(arguments, "id")
            ?? throw new InvalidOperationException("id must be an ingest item id.");
        var item = await ingest.GetAsync(itemId, cancellationToken);
        if (item is null)
        {
            return Failure(id, "No ingest item with that id. Ids come from list_ingest.");
        }

        return Content(id, new JsonObject
        {
            ["id"] = item.Id,
            ["title"] = item.MediaTitle ?? item.TargetTitle ?? item.DownloadName,
            ["stage"] = item.Stage,
            ["status"] = item.Status,
            ["stagesCompleted"] = new JsonArray([.. item.StagesCompleted.Select(stage => (JsonNode)stage!)]),
            ["attemptCount"] = item.AttemptCount,
            ["lastError"] = item.LastError,
            ["nextAttemptAt"] = item.NextAttemptAt?.ToString("O"),
            ["downloadId"] = item.DownloadId,
            ["downloadName"] = item.DownloadName,
            ["sourceFiles"] = new JsonArray([.. item.SourceFiles.Select(file => (JsonNode)new JsonObject
            {
                ["id"] = file.Id,
                ["path"] = file.RelativePath,
            })]),
        });
    }

    private async Task<JsonObject> ServerStatusAsync(CancellationToken cancellationToken)
    {
        var state = await scans.ListStateAsync(cancellationToken);
        var catalogs = await database.Catalogs.AsNoTracking()
            .Select(catalog => new { catalog.Id, catalog.Name })
            .ToListAsync(cancellationToken);
        var byStatus = await database.IngestItems.AsNoTracking()
            .GroupBy(item => item.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var rows = new JsonArray();
        foreach (var catalog in catalogs)
        {
            var scan = state.FirstOrDefault(entry => entry.CatalogId == catalog.Id);
            rows.Add(new JsonObject
            {
                ["id"] = catalog.Id,
                ["name"] = catalog.Name,
                ["neverScanned"] = scan?.NeverScanned ?? true,
                ["scanning"] = scan?.Scanning ?? false,
                ["lastScannedAt"] = scan?.LastCompletedAt?.ToString("O"),
            });
        }

        var pipeline = new JsonObject();
        foreach (var status in Enum.GetValues<IngestStatus>())
        {
            pipeline[status.ToString()] = byStatus.FirstOrDefault(row => row.Status == status)?.Count ?? 0;
        }

        return new JsonObject { ["catalogs"] = rows, ["pipeline"] = pipeline };
    }

    private static MediaKind? ParseKind(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" => null,
        "movie" or "movies" => MediaKind.Movie,
        "series" or "show" or "shows" => MediaKind.Series,
        _ => throw new InvalidOperationException($"kind must be 'movie' or 'series', not '{value}'."),
    };

    /// <summary>
    /// Parses a named value, refusing anything unrecognized.
    /// </summary>
    /// <remarks>
    /// An unknown filter silently dropped is the worst outcome available: the call succeeds, the filter
    /// does not apply, and "nothing is failing" comes back as a list of everything.
    /// </remarks>
    private static TEnum? ParseEnum<TEnum>(string? value, string argument) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"{argument} must be one of {string.Join(", ", Enum.GetNames<TEnum>())}, not '{value}'.");
    }
}
