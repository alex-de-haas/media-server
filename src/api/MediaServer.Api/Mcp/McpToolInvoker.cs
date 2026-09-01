using System.Text.Json.Nodes;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Metadata;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Torrents;
using MediaServer.Api.Watchlist;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using static MediaServer.Api.Mcp.McpProtocol;

namespace MediaServer.Api.Mcp;

/// <summary>Declares the tools and runs them against this server's own services.</summary>
/// <remarks>
/// A wide constructor on purpose: this is the one place that fans out to the services behind the
/// tools, and giving it a facade of its own would only move the list somewhere less obvious.
/// </remarks>
public sealed class McpToolInvoker(
    MediaServerDbContext database,
    LibraryReadService library,
    IngestService ingest,
    CatalogScanCoordinator scans,
    CatalogService catalogs,
    TorrentService torrents,
    RecommendationFeedService recommendations,
    WatchlistService watchlist,
    IMetadataProvider metadata)
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
            "list_shelf",
            "The ready-made rails: 'recent' is what was added lately, 'resume' is what is part-watched, "
            + "'nextup' is the next unwatched episode of each series in progress. Prefer these to a "
            + "search when the question is 'what should I carry on with'.",
            new JsonObject
            {
                ["shelf"] = Prop("string", "recent, resume or nextup."),
                ["limit"] = Prop("integer", "Maximum rows. Capped at 100; the default is 20."),
            },
            "shelf"),
        Tool(
            "list_recommendations",
            "What this server suggests watching, from the operator's own history — or from one named "
            + "title when `seed` is given, which is how 'something like this film' is answered. Reach "
            + "for this rather than ranking search results by hand: the engine already knows what was "
            + "watched, what was hidden, and the operator's popularity bias, and a hand-made ranking "
            + "will disagree with what the web UI shows.",
            new JsonObject
            {
                ["seed"] = Prop("string", "A library item id to recommend from, instead of watch history."),
                ["kind"] = Prop("string", "Restrict to 'movie' or 'series'."),
                ["limit"] = Prop("integer", "Maximum rows. Capped at 100; the default is 20."),
            }),
        Tool(
            "search_ingest_candidates",
            "Candidate identities for one pipeline item, searched by its own parsed title or by a title "
            + "you supply. This is the list to show an operator who has to say which film an item is; "
            + "the reference it returns is what repairs the match.",
            new JsonObject
            {
                ["id"] = Prop("string", "The ingest item id."),
                ["title"] = Prop("string", "Search this instead of the item's parsed title."),
                ["year"] = Prop("integer", "A year hint. Not a hard filter."),
            },
            "id"),
        Tool(
            "search_metadata",
            "Searches the metadata provider for a title, returning provider references. Use it to turn a "
            + "name into the reference a watchlist entry or a match needs. This searches the provider, "
            + "not this library — search_library answers what is held here.",
            new JsonObject
            {
                ["title"] = Prop("string", "The title to look up."),
                ["kind"] = Prop("string", "movie or series. Defaults to movie."),
                ["year"] = Prop("integer", "A year hint. Not a hard filter."),
            },
            "title"),
        Tool(
            "list_downloads",
            "Downloads and their progress: state, percent complete, estimated seconds remaining, and the "
            + "catalog each is bound for. A download's name is its release name — to find one by the "
            + "title someone would say out loud, use list_ingest and follow its downloadId.",
            new JsonObject
            {
                ["limit"] = Prop("integer", "Maximum rows. Capped at 200; the default is 50."),
            }),
        Tool(
            "list_catalogs",
            "The catalogs this server holds, what each has used on disk, and how much room is left on "
            + "the volumes behind them — plus whether each has ever been scanned.",
            []),
        Tool(
            "get_release_calendar",
            "Dated releases for the titles the operator tracks, within a window. For a title that is "
            + "*not* tracked, use preview_release instead — this answers only for the watchlist.",
            new JsonObject
            {
                ["from"] = Prop("string", "Start date, YYYY-MM-DD. Defaults to today."),
                ["to"] = Prop("string", "End date, YYYY-MM-DD. Defaults to 90 days out."),
            }),
        Tool(
            "preview_release",
            "When a title comes out, for a title nobody is tracking yet — the question that usually "
            + "comes before adding one. Asks the provider directly and records nothing.",
            new JsonObject
            {
                ["provider"] = Prop("string", "Provider key, e.g. tmdb."),
                ["id"] = Prop("string", "The provider's id for the title."),
                ["kind"] = Prop("string", "movie or series."),
            },
            "provider", "id", "kind"),
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
                "list_shelf" => Content(id, await ShelfAsync(arguments, appUserId, cancellationToken)),
                "list_recommendations" => Content(id, await RecommendationsAsync(arguments, appUserId, cancellationToken)),
                "search_ingest_candidates" => await IngestCandidatesAsync(id, arguments, cancellationToken),
                "search_metadata" => Content(id, await SearchMetadataAsync(arguments, cancellationToken)),
                "list_downloads" => Content(id, await DownloadsAsync(arguments, cancellationToken)),
                "list_catalogs" => Content(id, await CatalogsAsync(cancellationToken)),
                "get_release_calendar" => Content(id, await CalendarAsync(arguments, appUserId, cancellationToken)),
                "preview_release" => await PreviewReleaseAsync(id, arguments, cancellationToken),
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

    private async Task<JsonObject> ShelfAsync(
        JsonNode? arguments, int? appUserId, CancellationToken cancellationToken)
    {
        var shelf = (Str(arguments, "shelf") ?? string.Empty).ToLowerInvariant();
        var limit = Math.Clamp(Int(arguments, "limit") ?? 20, 1, 100);

        // Recent is the library's own order and belongs to nobody. The other two are a person's place in
        // a story, so answering them without one would be inventing a viewer.
        if (shelf is "resume" or "nextup" && appUserId is null)
        {
            throw new McpRefusedException($"The '{shelf}' shelf is per person, and this call carried no Hosty user.");
        }

        var rows = new JsonArray();
        switch (shelf)
        {
            case "recent":
                foreach (var item in await library.GetRecentAsync(limit, appUserId, cancellationToken))
                {
                    rows.Add(new JsonObject
                    {
                        ["id"] = item.Id, ["kind"] = item.Kind, ["title"] = item.Title, ["year"] = item.Year,
                    });
                }

                break;
            case "resume":
            case "nextup":
                var rail = shelf == "resume"
                    ? await library.GetResumeAsync(appUserId!.Value, limit, cancellationToken)
                    : await library.GetNextUpAsync(appUserId!.Value, limit, cancellationToken);
                foreach (var item in rail)
                {
                    rows.Add(new JsonObject
                    {
                        ["id"] = item.Id, ["kind"] = item.Kind, ["title"] = item.Title,
                        ["subtitle"] = item.Subtitle, ["seriesId"] = item.NavId,
                    });
                }

                break;
            default:
                throw new InvalidOperationException($"shelf must be recent, resume or nextup, not '{shelf}'.");
        }

        // No total to report: a rail is a fixed-length view by definition, not a page of something
        // larger, so a window here would invent a "rest" that does not exist.
        return new JsonObject { ["shelf"] = shelf, ["items"] = rows };
    }

    private async Task<JsonObject> RecommendationsAsync(
        JsonNode? arguments, int? appUserId, CancellationToken cancellationToken)
    {
        if (appUserId is null)
        {
            throw new McpRefusedException("Recommendations are per person, and this call carried no Hosty user.");
        }

        var seed = Id(arguments, "seed");
        var feed = await recommendations.BuildAsync(
            appUserId.Value, ParseRecommendationKind(Str(arguments, "kind")),
            Math.Clamp(Int(arguments, "limit") ?? 20, 1, 100), cancellationToken, seed);
        if (feed is null)
        {
            // Refused rather than answered with the ordinary feed: someone who asked for "something like
            // this film" and silently got their usual suggestions cannot tell a different question was
            // answered.
            throw new McpRefusedException(
                "That title cannot seed a recommendation — it has no provider identity, or it is an "
                + "episode, and the provider only answers 'what is like this show'.");
        }

        var rows = new JsonArray();
        foreach (var item in feed.Items)
        {
            rows.Add(new JsonObject
            {
                ["title"] = item.Title,
                ["year"] = item.Year,
                ["kind"] = item.Kind.ToString(),
                ["tmdbId"] = item.TmdbId,
                ["inLibrary"] = item.InLibrary,
                ["heldLibraryItemId"] = item.MediaItemId,
                // The reason is data the client phrases; flattened here so a model can say "because you
                // watched X" without needing to know the vocabulary.
                ["reason"] = item.Reason is { } reason
                    ? new JsonObject
                    {
                        ["kind"] = reason.Kind, ["detail"] = reason.Detail, ["rating"] = reason.Rating,
                    }
                    : null,
            });
        }

        return new JsonObject
        {
            ["seededBy"] = seed is null ? "watch history" : "the named title",
            ["items"] = rows,
        };
    }

    private async Task<IResult> IngestCandidatesAsync(
        JsonNode? id, JsonNode? arguments, CancellationToken cancellationToken)
    {
        var itemId = Id(arguments, "id")
            ?? throw new InvalidOperationException("id must be an ingest item id.");
        var item = await ingest.GetAsync(itemId, cancellationToken);
        if (item is null)
        {
            return Failure(id, "No ingest item with that id. Ids come from list_ingest.");
        }

        // The item's own parse is the starting point, which is what makes this better than a bare
        // provider search: it already reflects what the release name says.
        var title = Str(arguments, "title") ?? item.MediaTitle ?? item.TargetTitle ?? item.DownloadName;
        if (string.IsNullOrWhiteSpace(title))
        {
            return Failure(id, "This item has no title to search by. Pass one explicitly.");
        }

        var candidates = await ingest.SearchAsync(
            itemId, new MetadataSearchRequest(title, Int(arguments, "year"), null), cancellationToken);
        return candidates is null
            ? Failure(id, "That item is no longer searchable — it may already be organized.")
            : Content(id, new JsonObject
            {
                ["searchedFor"] = title,
                ["candidates"] = Candidates(candidates),
            });
    }

    private async Task<JsonObject> SearchMetadataAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        var title = Str(arguments, "title")
            ?? throw new InvalidOperationException("title is required.");
        var kind = ParseKind(Str(arguments, "kind")) ?? MediaKind.Movie;
        var year = Int(arguments, "year");

        var results = await metadata.SearchAsync(new MediaQuery(kind, title.Trim(), year), cancellationToken);
        if (results.Count == 0 && year is not null)
        {
            // The year is a hint, not a filter: a title whose release date is unset or disagrees returns
            // nothing under a year-constrained search, and reporting that as "no such title" would be a
            // claim about the provider's catalogue rather than about the query.
            results = await metadata.SearchAsync(new MediaQuery(kind, title.Trim(), null), cancellationToken);
        }

        return new JsonObject { ["candidates"] = Candidates(results) };
    }

    private static JsonArray Candidates(IReadOnlyList<MetadataCandidate> candidates) =>
        [.. candidates.Select(candidate => (JsonNode)new JsonObject
        {
            ["provider"] = candidate.Reference.Provider,
            ["providerId"] = candidate.Reference.Id,
            ["title"] = candidate.Title,
            ["year"] = candidate.Year,
            ["score"] = candidate.Score,
        })];

    private async Task<JsonObject> DownloadsAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(Int(arguments, "limit") ?? 50, 1, 200);
        var all = await torrents.ListAsync(cancellationToken);

        var rows = new JsonArray();
        foreach (var download in all.Take(limit))
        {
            rows.Add(new JsonObject
            {
                ["id"] = download.Id,
                ["name"] = download.Name,
                ["catalogId"] = download.CatalogId,
                ["state"] = download.State,
                ["percentComplete"] = download.PercentComplete,
                ["etaSeconds"] = download.EtaSeconds,
                ["sizeBytes"] = download.SizeBytes,
            });
        }

        return WithWindow(new JsonObject { ["downloads"] = rows }, rows.Count, all.Count, limit, 0);
    }

    private async Task<JsonObject> CatalogsAsync(CancellationToken cancellationToken)
    {
        var all = await catalogs.ListAsync(cancellationToken);
        var usage = await catalogs.ListUsageAsync(cancellationToken);
        var state = await scans.ListStateAsync(cancellationToken);

        var rows = new JsonArray();
        foreach (var catalog in all)
        {
            var scan = state.FirstOrDefault(entry => entry.CatalogId == catalog.Id);
            var used = usage.SelectMany(volume => volume.Catalogs)
                .FirstOrDefault(entry => entry.Id == catalog.Id)?.UsedBytes;
            rows.Add(new JsonObject
            {
                ["id"] = catalog.Id,
                ["name"] = catalog.Name,
                ["type"] = catalog.Type.ToString(),
                ["usedBytes"] = used,
                ["neverScanned"] = scan?.NeverScanned ?? true,
                ["scanning"] = scan?.Scanning ?? false,
            });
        }

        var volumes = new JsonArray([.. usage.Select(volume => (JsonNode)new JsonObject
        {
            ["label"] = volume.Label,
            ["freeBytes"] = volume.FreeBytes,
        })]);

        return new JsonObject { ["catalogs"] = rows, ["volumes"] = volumes };
    }

    private async Task<JsonObject> CalendarAsync(
        JsonNode? arguments, int? appUserId, CancellationToken cancellationToken)
    {
        if (appUserId is null)
        {
            throw new McpRefusedException("The calendar is per person, and this call carried no Hosty user.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = ParseDate(Str(arguments, "from")) ?? today;
        var to = ParseDate(Str(arguments, "to")) ?? today.AddDays(90);

        var events = await watchlist.CalendarAsync(appUserId.Value, from, to, cancellationToken);
        var rows = new JsonArray([.. events.Select(item => (JsonNode)new JsonObject
        {
            ["title"] = item.Title,
            ["kind"] = item.Kind.ToString(),
            ["releaseType"] = item.Type.ToString(),
            ["date"] = item.Date.ToString("yyyy-MM-dd"),
            ["season"] = item.Season,
            ["episode"] = item.Episode,
        })]);

        return WithNote(
            new JsonObject { ["from"] = from.ToString("yyyy-MM-dd"), ["to"] = to.ToString("yyyy-MM-dd"), ["events"] = rows },
            rows.Count == 0
                ? "Nothing dated in this window among the titles being tracked. A title nobody tracks "
                  + "never appears here however close its release — ask preview_release for one of those."
                : null);
    }

    private async Task<IResult> PreviewReleaseAsync(
        JsonNode? id, JsonNode? arguments, CancellationToken cancellationToken)
    {
        var provider = Str(arguments, "provider") ?? throw new InvalidOperationException("provider is required.");
        var providerId = Str(arguments, "id") ?? throw new InvalidOperationException("id is required.");
        var kind = ParseKind(Str(arguments, "kind"))
            ?? throw new InvalidOperationException("kind must be 'movie' or 'series'.");

        var preview = await watchlist.PreviewScheduleAsync(provider, providerId, kind, cancellationToken);
        if (preview is null)
        {
            // "No dates" would be a claim about the title. This is a report about the request.
            return Failure(id, "The provider gave no schedule for that title — it may be unknown to it, "
                + "or the lookup failed. This is not the same as the title having no release date.");
        }

        return Content(id, new JsonObject
        {
            ["title"] = preview.Title,
            ["year"] = preview.Year,
            ["status"] = preview.Status,
            ["dates"] = new JsonArray([.. preview.Dates.Select(date => (JsonNode)new JsonObject
            {
                ["region"] = date.Region,
                ["type"] = date.Type.ToString(),
                ["date"] = date.Date.ToString("yyyy-MM-dd"),
            })]),
            ["nextEpisode"] = preview.NextEpisode is { } next
                ? new JsonObject
                {
                    ["season"] = next.Season, ["episode"] = next.Episode,
                    ["airDate"] = next.AirDate.ToString("yyyy-MM-dd"),
                }
                : null,
        });
    }

    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParse(value, out var parsed) ? parsed : null;

    private static RecommendationKind? ParseRecommendationKind(string? value) => ParseKind(value) switch
    {
        MediaKind.Movie => RecommendationKind.Movie,
        MediaKind.Series => RecommendationKind.Series,
        _ => null,
    };

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
