using System.Text.Json.Nodes;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Metadata;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Torrents;
using MediaServer.Api.WatchHistory;
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
    WatchHistoryCalendarService watchHistory,
    IMetadataProvider metadata,
    UserDataService userData,
    CatalogRefreshCoordinator refreshes)
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
            "list_watch_history",
            "What was actually watched, and when. Distinct from the watched flag on a title, which "
            + "says only that something was finished at some point and carries no date: this answers "
            + "'what did I watch last week', 'when did I see this', and the same question about any "
            + "period, however far back. Newest first.",
            new JsonObject
            {
                ["from"] = Prop("string", "Start of the period, YYYY-MM-DD. Defaults to 30 days ago."),
                ["to"] = Prop("string", "End of the period, exclusive, YYYY-MM-DD. Defaults to tomorrow."),
                ["limit"] = Prop("integer", "Maximum plays. Capped at 200; the default is 50."),
                ["offset"] = Prop("integer", "Plays to skip, for paging through a long period."),
            }),
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
        WriteTool(
            "set_title_state",
            "Records what the operator did with a title: watched, favourite, or a 1-5 star verdict. Only "
            + "the fields you pass change; the rest are left alone.",
            new JsonObject
            {
                ["id"] = Prop("string", "The library item id."),
                ["watched"] = Prop("boolean", "Mark watched or unwatched."),
                ["favorite"] = Prop("boolean", "Add to or remove from favourites."),
                ["userRating"] = Prop("integer", "A 1-5 star verdict. Pass 0 to clear it."),
            },
            idempotent: true,
            "id"),
        WriteTool(
            "manage_watchlist",
            "Tracks or stops tracking a title, so its release dates reach the calendar. Adding needs a "
            + "provider reference — search_metadata turns a name into one.",
            new JsonObject
            {
                ["action"] = Prop("string", "add or remove."),
                ["provider"] = Prop("string", "Provider key for add, e.g. tmdb."),
                ["providerId"] = Prop("string", "The provider's id, for add."),
                ["kind"] = Prop("string", "movie or series, for add."),
                ["title"] = Prop("string", "Display title, for add."),
                ["year"] = Prop("integer", "Release year, for add."),
                ["entryId"] = Prop("string", "The watchlist entry id, for remove."),
            },
            idempotent: false,
            "action"),
        WriteTool(
            "add_torrent",
            "Starts a download into a catalog. The catalog is required and cannot be guessed — call "
            + "list_catalogs and ask which one if it was not named. Answers 'accepted': the download is "
            + "queued, not finished, and list_downloads reports its progress.",
            new JsonObject
            {
                ["catalogId"] = Prop("string", "The catalog to download into. Required."),
                ["magnet"] = Prop("string", "A magnet link."),
                ["keepSeeding"] = Prop("boolean", "Keep seeding after the download completes."),
            },
            idempotent: false,
            "catalogId", "magnet"),
        WriteTool(
            "control_download",
            "Pauses, resumes, or stops seeding one download.",
            new JsonObject
            {
                ["id"] = Prop("string", "The download id, from list_downloads."),
                ["action"] = Prop("string", "pause, resume or stop_seeding."),
            },
            idempotent: true,
            "id", "action"),
        WriteTool(
            "match_ingest_item",
            "Tells the pipeline which title an item is, so it can finish. Read get_ingest_item first: "
            + "the source file ids a match names come from there, and a guessed id fails. One group per "
            + "identity — a pack holding several films is several groups. For episodes use kind "
            + "'episode' with the *series* provider id, and give each file its season and episode; "
            + "'series' means the whole thing is one work, and its files resolve as a movie would.",
            new JsonObject
            {
                ["id"] = Prop("string", "The ingest item id."),
                ["groups"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "One entry per identity the item resolves to.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["provider"] = Prop("string", "Provider key, e.g. tmdb."),
                            ["providerId"] = Prop("string", "The provider's id."),
                            ["kind"] = Prop("string", "movie, series, or episode for a per-episode match."),
                            ["title"] = Prop("string", "Display title."),
                            ["year"] = Prop("integer", "Release year."),
                            ["files"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "Source files belonging to this identity.",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["sourceFileId"] = Prop("string", "From get_ingest_item."),
                                        ["season"] = Prop("integer", "Episode matches only."),
                                        ["episode"] = Prop("integer", "Episode matches only."),
                                    },
                                },
                            },
                        },
                    },
                },
            },
            idempotent: false,
            "id", "groups"),
        WriteTool(
            "advance_ingest_item",
            "Pushes a stalled pipeline item along: 'retry' re-runs the stage that failed, 'retarget' "
            + "re-homes an item parked over a cross-catalog conflict.",
            new JsonObject
            {
                ["id"] = Prop("string", "The ingest item id."),
                ["action"] = Prop("string", "retry or retarget."),
            },
            idempotent: false,
            "id", "action"),
        WriteTool(
            "scan_catalog",
            "Starts a catalog scan without waiting for it. Answers 'accepted' with the job, or says a "
            + "scan is already running rather than starting a second. Omit the catalog to scan every one.",
            new JsonObject { ["catalogId"] = Prop("string", "One catalog, or omit for all of them.") },
            idempotent: true),
        WriteTool(
            "refresh_metadata",
            "Re-fetches provider metadata and artwork for a catalog. Answers 'accepted': it runs in the "
            + "background, and a refresh already under way is reported rather than duplicated.",
            new JsonObject { ["catalogId"] = Prop("string", "One catalog, or omit for all of them.") },
            idempotent: true),
        Tool(
            "get_server_status",
            "What this server is doing and whether it can answer for itself: catalogs and whether each "
            + "has ever been scanned, scans running now, and pipeline items per status. Check it before "
            + "concluding a title is absent — a catalog nothing has scanned holds nothing this server "
            + "knows about, which is not the same as holding nothing.",
            []),
    ];

    /// <summary>
    /// Tools whose HTTP twins require <see cref="AppRoles.AdminPolicy"/>.
    /// </summary>
    /// <remarks>
    /// The endpoint requires an authenticated user and nothing more, which is right for reading a
    /// library and wrong for maintenance: the catalog routes these call are admin-only, and calling the
    /// coordinators in-process walks around that. Without this list an ordinary host user can start work
    /// the HTTP surface would refuse them — the app authorizing is the half Core cannot do, and this is
    /// where it was missing.
    /// </remarks>
    private static readonly HashSet<string> AdminOnlyTools = new(StringComparer.Ordinal)
    {
        "scan_catalog", "refresh_metadata",
    };

    public async Task<IResult> CallAsync(
        JsonNode? id, JsonNode? parameters, int? appUserId, bool isAdministrator,
        CancellationToken cancellationToken)
    {
        var name = Str(parameters, "name");
        var arguments = parameters?["arguments"];

        if (name is not null && AdminOnlyTools.Contains(name) && !isAdministrator)
        {
            return Failure(id, $"{name} is an administrator action on this server, and the calling user "
                + "is not one. A host administrator can run it from the web interface.");
        }

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
                "list_watch_history" => Content(id, await WatchHistoryAsync(arguments, appUserId, cancellationToken)),
                "list_recommendations" => Content(id, await RecommendationsAsync(arguments, appUserId, cancellationToken)),
                "search_ingest_candidates" => await IngestCandidatesAsync(id, arguments, cancellationToken),
                "search_metadata" => Content(id, await SearchMetadataAsync(arguments, cancellationToken)),
                "list_downloads" => Content(id, await DownloadsAsync(arguments, cancellationToken)),
                "list_catalogs" => Content(id, await CatalogsAsync(cancellationToken)),
                "get_release_calendar" => Content(id, await CalendarAsync(arguments, appUserId, cancellationToken)),
                "preview_release" => await PreviewReleaseAsync(id, arguments, cancellationToken),
                "set_title_state" => await SetTitleStateAsync(id, arguments, appUserId, cancellationToken),
                "manage_watchlist" => await ManageWatchlistAsync(id, arguments, appUserId, cancellationToken),
                "add_torrent" => await AddTorrentAsync(id, arguments, cancellationToken),
                "control_download" => await ControlDownloadAsync(id, arguments, cancellationToken),
                "match_ingest_item" => await MatchIngestAsync(id, arguments, cancellationToken),
                "advance_ingest_item" => await AdvanceIngestAsync(id, arguments, cancellationToken),
                "scan_catalog" => Content(id, await ScanAsync(arguments, cancellationToken)),
                "refresh_metadata" => Content(id, await RefreshAsync(arguments, cancellationToken)),
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

    private async Task<JsonObject> WatchHistoryAsync(
        JsonNode? arguments, int? appUserId, CancellationToken cancellationToken)
    {
        if (appUserId is null)
        {
            throw new McpRefusedException("Watch history is per person, and this call carried no Hosty user.");
        }

        // Local midnight on both sides, defaults included. Anchoring the defaults to `UtcNow` and
        // shifting the instant — which is what this did first — contradicts the schema's own
        // YYYY-MM-DD promise: an evening west of Greenwich is already tomorrow in UTC, so "the last 30
        // days" silently became a different 30 days than the ones the caller would name.
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = RequiredLocalStart(arguments, "from") ?? LocalStart(today.AddDays(-30));
        // Exclusive, and defaulted past today so "what did I watch today" is not an empty answer about
        // a period that ends before the plays it is asking about.
        var to = RequiredLocalStart(arguments, "to") ?? LocalStart(today.AddDays(1));
        var limit = Math.Clamp(Int(arguments, "limit") ?? 50, 1, 200);
        var offset = Math.Max(0, Int(arguments, "offset") ?? 0);

        if (to <= from)
        {
            throw new InvalidOperationException("'to' must be after 'from'.");
        }

        var page = await watchHistory.SearchAsync(appUserId.Value, from, to, limit, offset, cancellationToken);

        var rows = new JsonArray();
        foreach (var play in page.Events)
        {
            rows.Add(new JsonObject
            {
                ["watchedAt"] = play.WatchedAt.ToString("O"),
                ["title"] = play.Title,
                ["kind"] = play.Kind,
                ["seriesTitle"] = play.SeriesTitle,
                ["season"] = play.SeasonNumber,
                ["episode"] = play.EpisodeNumber,
                ["libraryItemId"] = play.MediaItemId,
                // Where the play came from — a local playback or an imported provider history.
                ["origin"] = play.Origin,
            });
        }

        var payload = WithWindow(
            new JsonObject
            {
                ["from"] = from.ToString("O"),
                ["to"] = to.ToString("O"),
                ["plays"] = rows,
            },
            rows.Count, page.Total, limit, offset);

        // Undated plays can never fall inside a period, so every answer about one omits them. Said
        // plainly, because "you watched nothing that week" and "you watched nothing that week that
        // carries a date" are different statements and only the second is true.
        return WithNote(payload, page.UndatedTotal > 0
            ? $"{page.UndatedTotal} play(s) in this library carry no date at all — imported from a "
              + "provider that reported none — and cannot appear in any period, including this one."
            : null);
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

    /// <summary>Midnight of a date, in the server's own zone.</summary>
    /// <remarks>
    /// Local rather than UTC on purpose: "yesterday" is a day in the operator's life, not a UTC
    /// interval. Reading the boundary in UTC shifts it by the offset, which for anyone west of
    /// Greenwich quietly moves an evening's viewing into the wrong day.
    /// </remarks>
    private static DateTimeOffset LocalStart(DateOnly date)
        => new(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));

    /// <summary>
    /// A supplied date boundary, or null when the argument was absent — but a failure when it was
    /// present and unreadable.
    /// </summary>
    /// <remarks>
    /// The distinction is the point. Treating an unparseable date as absent falls back to the default
    /// window, so `from: "2026-13-01"` would answer confidently about the last thirty days instead —
    /// a wrong answer to a question nobody asked, with nothing in the reply to say so. A typo has to
    /// be a refusal.
    /// </remarks>
    private static DateTimeOffset? RequiredLocalStart(JsonNode? arguments, string name)
    {
        var raw = Str(arguments, name);
        if (raw is null)
        {
            return null;
        }

        return ParseDate(raw) is { } date
            ? LocalStart(date)
            : throw new InvalidOperationException(
                $"'{name}' must be a date as YYYY-MM-DD, not '{raw}'.");
    }

    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParse(value, out var parsed) ? parsed : null;

    private static RecommendationKind? ParseRecommendationKind(string? value) => ParseKind(value) switch
    {
        MediaKind.Movie => RecommendationKind.Movie,
        MediaKind.Series => RecommendationKind.Series,
        _ => null,
    };

    private async Task<IResult> SetTitleStateAsync(
        JsonNode? id, JsonNode? arguments, int? appUserId, CancellationToken cancellationToken)
    {
        if (appUserId is null)
        {
            throw new McpRefusedException("Watch state, favourites and ratings are per person, and this call carried no Hosty user.");
        }

        var itemId = Id(arguments, "id") ?? throw new InvalidOperationException("id must be a library item id.");
        var watched = Bool(arguments, "watched");
        var favorite = Bool(arguments, "favorite");
        var rating = Int(arguments, "userRating");
        if (watched is null && favorite is null && rating is null)
        {
            // Nothing asked for is not the same as everything cleared. Silently writing defaults would
            // wipe a rating the caller never mentioned.
            return Failure(id, "Pass at least one of watched, favorite or userRating.");
        }

        var changed = new JsonArray();
        if (watched is { } played)
        {
            if (await userData.SetPlayedAsync(appUserId.Value, itemId, played, null, cancellationToken) is null)
            {
                return Failure(id, "No title with that id. Ids come from search_library.");
            }

            changed.Add("watched");
        }

        if (favorite is { } isFavorite)
        {
            await userData.SetFavoriteAsync(appUserId.Value, itemId, isFavorite, cancellationToken);
            changed.Add("favorite");
        }

        if (rating is { } stars)
        {
            // Zero clears rather than scores: the scale starts at one, so there is no way to say "no
            // opinion" with a value on it.
            var result = await userData.SetRatingAsync(appUserId.Value, itemId, stars == 0 ? null : stars, cancellationToken);
            if (result.Status != SetRatingStatus.Applied)
            {
                return Failure(id, $"That rating was refused: {result.Status}. Ratings are 1-5, or 0 to clear.");
            }

            changed.Add("userRating");
        }

        return Content(id, new JsonObject { ["id"] = itemId, ["changed"] = changed });
    }

    private async Task<IResult> ManageWatchlistAsync(
        JsonNode? id, JsonNode? arguments, int? appUserId, CancellationToken cancellationToken)
    {
        if (appUserId is null)
        {
            throw new McpRefusedException("A watchlist belongs to a person, and this call carried no Hosty user.");
        }

        var action = (Str(arguments, "action") ?? string.Empty).ToLowerInvariant();
        switch (action)
        {
            case "add":
                var provider = Str(arguments, "provider") ?? throw new InvalidOperationException("provider is required to add.");
                var providerId = Str(arguments, "providerId") ?? throw new InvalidOperationException("providerId is required to add.");
                var kind = ParseKind(Str(arguments, "kind")) ?? throw new InvalidOperationException("kind must be 'movie' or 'series'.");
                var added = await watchlist.AddAsync(
                    appUserId.Value,
                    new AddWatchlistRequest(
                        new ProviderRefBody(provider, providerId), kind, null, null, null, null,
                        Str(arguments, "title"), Int(arguments, "year"), null),
                    cancellationToken);
                return Content(id, new JsonObject
                {
                    ["action"] = "add",
                    ["entryId"] = added.Item?.Id,
                    // Dates arrive from the provider on a background sync, so "tracked" is not yet the
                    // same as "dated" — saying so keeps the next question ("when does it come out") from
                    // reading an empty calendar as an answer.
                    ["note"] = "Tracked. Release dates are fetched in the background, so the calendar may "
                        + "not show this title for a short while.",
                });
            case "remove":
                var entryId = Id(arguments, "entryId") ?? throw new InvalidOperationException("entryId is required to remove.");
                return await watchlist.RemoveAsync(appUserId.Value, entryId, cancellationToken)
                    ? Content(id, new JsonObject { ["action"] = "remove", ["entryId"] = entryId })
                    : Failure(id, "No watchlist entry with that id for this user.");
            default:
                throw new InvalidOperationException($"action must be 'add' or 'remove', not '{action}'.");
        }
    }

    private async Task<IResult> AddTorrentAsync(JsonNode? id, JsonNode? arguments, CancellationToken cancellationToken)
    {
        var catalogId = Id(arguments, "catalogId")
            ?? throw new InvalidOperationException("catalogId is required — call list_catalogs and ask which one.");
        var magnet = Str(arguments, "magnet") ?? throw new InvalidOperationException("magnet is required.");

        var download = await torrents.AddAsync(
            new AddTorrentRequest(catalogId, magnet, null, Bool(arguments, "keepSeeding")), cancellationToken);

        return Content(id, new JsonObject
        {
            // Accepted, not done. Reporting an enqueue as a completed download is a lie the operator
            // only discovers when the film is not there.
            ["outcome"] = "accepted",
            ["downloadId"] = download.Id,
            ["name"] = download.Name,
            ["note"] = "Queued. list_downloads reports progress; list_ingest reports what happens after "
                + "it finishes downloading.",
        });
    }

    private async Task<IResult> ControlDownloadAsync(JsonNode? id, JsonNode? arguments, CancellationToken cancellationToken)
    {
        var downloadId = Id(arguments, "id") ?? throw new InvalidOperationException("id must be a download id.");
        var action = (Str(arguments, "action") ?? string.Empty).ToLowerInvariant();
        var ok = action switch
        {
            "pause" => await torrents.PauseAsync(downloadId, cancellationToken),
            "resume" => await torrents.ResumeAsync(downloadId, cancellationToken),
            "stop_seeding" => await torrents.StopSeedingAsync(downloadId, cancellationToken),
            _ => throw new InvalidOperationException($"action must be pause, resume or stop_seeding, not '{action}'."),
        };

        return ok
            ? Content(id, new JsonObject { ["id"] = downloadId, ["action"] = action })
            : Failure(id, "No download with that id, or it is not in a state that action applies to.");
    }

    private async Task<IResult> MatchIngestAsync(JsonNode? id, JsonNode? arguments, CancellationToken cancellationToken)
    {
        var itemId = Id(arguments, "id") ?? throw new InvalidOperationException("id must be an ingest item id.");
        if (arguments?["groups"] is not JsonArray groups || groups.Count == 0)
        {
            throw new InvalidOperationException("groups must name at least one identity.");
        }

        var parsed = new List<MatchGroupRequest>();
        foreach (var group in groups)
        {
            var files = new List<MatchFileRequest>();
            foreach (var file in group?["files"] as JsonArray ?? [])
            {
                files.Add(new MatchFileRequest(
                    Id(file, "sourceFileId")
                        ?? throw new InvalidOperationException("each file needs a sourceFileId from get_ingest_item."),
                    Int(file, "season"),
                    Int(file, "episode")));
            }

            parsed.Add(new MatchGroupRequest(
                ParseGroupKind(Str(group, "kind")),
                Str(group, "provider") ?? throw new InvalidOperationException("each group needs a provider."),
                Str(group, "providerId") ?? throw new InvalidOperationException("each group needs a providerId."),
                Str(group, "title") ?? string.Empty,
                Int(group, "year"),
                files));
        }

        var first = parsed[0];
        var outcome = await ingest.MatchAsync(
            itemId,
            new MatchRequest(first.Kind, first.Provider, first.ProviderId, first.Title, first.Year, first.Files, parsed),
            cancellationToken);

        return outcome == MatchOutcome.Matched
            ? Content(id, new JsonObject
            {
                ["outcome"] = "accepted",
                ["id"] = itemId,
                ["note"] = "Matched. The item resumes at the stage it was parked in; list_ingest shows it "
                    + "move, and it is not published until that completes.",
            })
            : Failure(id, $"The match was refused: {outcome}.");
    }

    private async Task<IResult> AdvanceIngestAsync(JsonNode? id, JsonNode? arguments, CancellationToken cancellationToken)
    {
        var itemId = Id(arguments, "id") ?? throw new InvalidOperationException("id must be an ingest item id.");
        var action = (Str(arguments, "action") ?? string.Empty).ToLowerInvariant();

        switch (action)
        {
            case "retry":
                return await ingest.RetryAsync(itemId, cancellationToken)
                    ? Content(id, Accepted(itemId, "retry", "Re-queued at the stage that failed."))
                    : Failure(id, "No ingest item with that id, or it is not in a state that can be retried.");
            case "retarget":
                var outcome = await ingest.RetargetAsync(itemId, cancellationToken);
                return outcome == RetargetOutcome.Retargeted
                    ? Content(id, Accepted(itemId, "retarget", "Re-homed to the catalog that holds the title."))
                    : Failure(id, $"Retarget was refused: {outcome}.");
            default:
                throw new InvalidOperationException($"action must be 'retry' or 'retarget', not '{action}'.");
        }
    }

    private static JsonObject Accepted(Guid itemId, string action, string note) => new()
    {
        ["outcome"] = "accepted",
        ["id"] = itemId,
        ["action"] = action,
        ["note"] = note + " The pipeline runs it in the background; list_ingest reports where it gets to.",
    };

    private async Task<JsonObject> ScanAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        if (Id(arguments, "catalogId") is not { } catalogId)
        {
            var started = await scans.RequestAllAsync(cancellationToken);
            return new JsonObject
            {
                ["outcome"] = "accepted",
                ["started"] = started,
                ["note"] = "Catalogs already scanning were left to the run they are in.",
            };
        }

        var result = await scans.RequestAsync(catalogId, cancellationToken);
        return result.Status switch
        {
            CatalogScanRequestStatus.Started => new JsonObject
            {
                ["outcome"] = "accepted",
                ["jobId"] = result.JobId,
                ["note"] = "Scanning in the background. get_server_status reports when it finishes.",
            },
            CatalogScanRequestStatus.AlreadyRunning => new JsonObject
            {
                ["outcome"] = "already-running",
                ["note"] = "A scan is already under way for that catalog; a second was not started.",
            },
            _ => new JsonObject { ["outcome"] = "not-found", ["note"] = "No catalog with that id." },
        };
    }

    private async Task<JsonObject> RefreshAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        if (Id(arguments, "catalogId") is not { } catalogId)
        {
            return new JsonObject
            {
                ["outcome"] = "accepted",
                ["started"] = await refreshes.RequestAllAsync(cancellationToken),
                ["note"] = "Catalogs already refreshing were left to the run they are in.",
            };
        }

        var result = await refreshes.RequestAsync(catalogId, cancellationToken);
        return result.Status switch
        {
            CatalogRefreshRequestStatus.Started => new JsonObject
            {
                ["outcome"] = "accepted",
                ["jobId"] = result.JobId,
                ["note"] = "Refreshing in the background.",
            },
            CatalogRefreshRequestStatus.AlreadyRunning => new JsonObject
            {
                ["outcome"] = "already-running",
                ["note"] = "A refresh is already under way for that catalog; a second was not started.",
            },
            _ => new JsonObject { ["outcome"] = "not-found", ["note"] = "No catalog with that id." },
        };
    }

    /// <summary>
    /// The kind of one match group, which admits <c>episode</c> where a filter does not.
    /// </summary>
    /// <remarks>
    /// The pipeline branches on exactly this: <c>MatchAsync</c> treats <see cref="MediaKind.Episode"/>
    /// as the episodic case and sends every other kind through movie resolution. A tool that accepted
    /// only movie and series could not repair an episode ingest at all — `series` would resolve each
    /// episode file as a film, which succeeds and is wrong.
    ///
    /// The provider id on an episode group is the *series* id. That is how the pipeline addresses
    /// episodes everywhere, and the tool's description says so, because the pairing reads oddly.
    /// </remarks>
    private static MediaKind ParseGroupKind(string? value) => value?.ToLowerInvariant() switch
    {
        "movie" or "movies" => MediaKind.Movie,
        "series" or "show" or "shows" => MediaKind.Series,
        "episode" or "episodes" => MediaKind.Episode,
        _ => throw new InvalidOperationException(
            $"each group needs kind 'movie', 'series' or 'episode', not '{value}'."),
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
