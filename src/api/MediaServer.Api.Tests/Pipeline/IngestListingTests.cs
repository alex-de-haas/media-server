using MediaServer.Api.Data;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// The filtered, windowed ingest listing. It exists for one question — "why has this not appeared
/// yet" — asked about a single title out of a pipeline that may hold thousands of rows, so what is
/// asserted here is mostly that the answer cannot mislead: the filter narrows, the total counts what
/// the window left behind, and a title is found by whichever of its three names the caller knows.
/// </summary>
public sealed class IngestListingTests
{
    [Fact]
    public async Task Filtering_by_status_narrows_the_list_and_no_filter_returns_everything()
    {
        // The pair is the test. A filter that returned everything would pass the first assertion alone,
        // and so would one that returned nothing if only the second were checked.
        using var harness = new PipelineTestHarness();
        var (failedId, catalogId, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Broken.Release.2021", "Broken.Release.2021/movie.mkv");
        await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Fine.Release.2021", "Fine.Release.2021/movie.mkv", catalogId);
        await SetStatusAsync(harness, failedId, IngestStatus.Failed);

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();

        var failed = await service.ListAsync(new IngestListQuery(Status: IngestStatus.Failed), CancellationToken.None);
        Assert.Equal(failedId, Assert.Single(failed.Items).Id);

        var everything = await service.ListAsync(new IngestListQuery(), CancellationToken.None);
        Assert.Equal(2, everything.Items.Count);
    }

    [Fact]
    public async Task The_total_counts_what_the_window_left_behind()
    {
        // Without this a full page and a complete answer are indistinguishable, which is how an agent
        // reports "there are two failures" when there are ninety and it was handed the newest two.
        using var harness = new PipelineTestHarness();
        Guid? catalogId = null;
        for (var i = 0; i < 5; i++)
        {
            var seeded = await harness.SeedCompletedDownloadAsync(
                CatalogType.Movie, $"Release.{i}.2021", $"Release.{i}.2021/movie.mkv", catalogId);
            catalogId = seeded.CatalogId;
        }

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();

        var windowed = await service.ListAsync(new IngestListQuery(Limit: 2), CancellationToken.None);
        Assert.Equal(2, windowed.Items.Count);
        Assert.Equal(5, windowed.Total);

        // Paired so that Total cannot simply be "however many items came back".
        var whole = await service.ListAsync(new IngestListQuery(Limit: 50), CancellationToken.None);
        Assert.Equal(5, whole.Items.Count);
        Assert.Equal(5, whole.Total);
    }

    [Fact]
    public async Task A_title_is_found_by_the_release_name_the_pinned_target_or_the_identified_title()
    {
        // The three names one item can have, and the caller knows only the one they would say out loud.
        // Searching just the identified title answers "no such download" for precisely the items worth
        // asking about — the ones still parked in review, which have no identity yet.
        using var harness = new PipelineTestHarness();
        var (releaseOnly, catalogId, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Oppenheimer.2023.2160p.WEB-DL", "Oppenheimer.2023.2160p.WEB-DL/movie.mkv");
        var (pinned, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Unreadable.Release.Name", "Unreadable.Release.Name/movie.mkv", catalogId);
        var (identified, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Another.Opaque.Name", "Another.Opaque.Name/movie.mkv", catalogId);

        await MutateAsync(harness, pinned, item => item.TargetTitle = "Barbie");
        var mediaItemId = await AddMediaItemAsync(harness, catalogId, "Dune");
        await MutateAsync(harness, identified, item => item.MediaItemId = mediaItemId);

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();

        Assert.Equal(releaseOnly, Assert.Single(
            (await service.ListAsync(new IngestListQuery(Title: "oppenheimer"), CancellationToken.None)).Items).Id);
        Assert.Equal(pinned, Assert.Single(
            (await service.ListAsync(new IngestListQuery(Title: "Barbie"), CancellationToken.None)).Items).Id);
        Assert.Equal(identified, Assert.Single(
            (await service.ListAsync(new IngestListQuery(Title: "Dune"), CancellationToken.None)).Items).Id);
    }

    [Fact]
    public async Task Paging_neither_drops_nor_repeats_a_row_when_timestamps_tie()
    {
        // CreatedAt alone is not a total order: items stamped in the same tick can come back in a
        // different order for one page than for the next, which shows up as one row appearing twice and
        // another never appearing at all. Seeded with a single timestamp so the tie is guaranteed rather
        // than hoped for.
        //
        // What this asserts is the property that matters — paging over the whole set yields each row
        // once. It does *not* prove the `ThenByDescending(Id)` tie-break is what delivers that: removing
        // the tie-break leaves this green, because SQLite happens to return these rows in a stable order.
        // The tie-break stays because the ordering is unspecified, not because a test caught its absence.
        using var harness = new PipelineTestHarness();
        Guid? catalogId = null;
        var seededIds = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var seeded = await harness.SeedCompletedDownloadAsync(
                CatalogType.Movie, $"Tied.{i}.2021", $"Tied.{i}.2021/movie.mkv", catalogId);
            catalogId = seeded.CatalogId;
            seededIds.Add(seeded.IngestId);
        }

        var stamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            await database.IngestItems.ForEachAsync(item => item.CreatedAt = stamp);
            await database.SaveChangesAsync();
        }

        using var reading = harness.CreateScope();
        var service = reading.ServiceProvider.GetRequiredService<IngestService>();

        var paged = new List<Guid>();
        for (var offset = 0; offset < 6; offset += 2)
        {
            var page = await service.ListAsync(new IngestListQuery(Limit: 2, Offset: offset), CancellationToken.None);
            paged.AddRange(page.Items.Select(item => item.Id));
        }

        Assert.Equal(6, paged.Distinct().Count());
        Assert.Equal(seededIds.OrderBy(id => id), paged.OrderBy(id => id));
    }

    [Fact]
    public async Task A_wildcard_typed_by_the_caller_is_matched_literally()
    {
        // Unescaped, "%" is a pattern that matches every row, so the tool would answer a search for a
        // title nobody has with the entire pipeline — wrong in the direction that looks like success.
        using var harness = new PipelineTestHarness();
        var (percent, catalogId, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "100% Wolf (2020)", "100% Wolf (2020)/movie.mkv");
        await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Unrelated.Release.2021", "Unrelated.Release.2021/movie.mkv", catalogId);

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();

        // A bare "%" is the term that tells the two behaviours apart. Searching "100%" does not: even
        // unescaped it still fails to match an unrelated row, so it passes whether or not the escaping
        // works — which is exactly how the first version of this test proved nothing.
        var page = await service.ListAsync(new IngestListQuery(Title: "%"), CancellationToken.None);

        Assert.Equal(percent, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task The_unwindowed_call_is_not_the_windowed_one_with_a_large_limit()
    {
        // The regression this guards was mine: routing the existing call through the window as
        // `Limit: int.MaxValue` clamps to the 500 ceiling, so a caller that had always received every row
        // would quietly start receiving the newest five hundred. Asserted at the boundary rather than
        // with 501 fixtures, by proving the ceiling applies to one path and not the other.
        using var harness = new PipelineTestHarness();
        Guid? catalogId = null;
        for (var i = 0; i < 3; i++)
        {
            var seeded = await harness.SeedCompletedDownloadAsync(
                CatalogType.Movie, $"Unwindowed.{i}.2021", $"Unwindowed.{i}.2021/movie.mkv", catalogId);
            catalogId = seeded.CatalogId;
        }

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();

        Assert.Equal(3, (await service.ListAsync(CancellationToken.None)).Count);

        // The windowed path with no limit takes its default instead of everything, which is the whole
        // reason the two cannot be the same call.
        var defaulted = await service.ListAsync(new IngestListQuery(), CancellationToken.None);
        Assert.Equal(50, defaulted.Limit);
    }

    [Fact]
    public async Task A_title_matches_regardless_of_case_in_any_alphabet()
    {
        // Measured before it was fixed: SQLite's own LIKE folds A-Z and nothing else, so this search
        // returned the row for "Оппенгеймер" and nothing for "оппенгеймер" — and for a Russian-language
        // library that is the difference between a working search and one that reports absence as fact.
        using var harness = new PipelineTestHarness();
        var (cyrillic, catalogId, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Оппенгеймер.2023.WEB-DL", "Оппенгеймер.2023.WEB-DL/movie.mkv");
        var (latin, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Barbie.2023.WEB-DL", "Barbie.2023.WEB-DL/movie.mkv", catalogId);

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();

        foreach (var term in new[] { "оппенгеймер", "ОППЕНГЕЙМЕР", "ОпПеНгЕйМеР" })
        {
            var page = await service.ListAsync(new IngestListQuery(Title: term), CancellationToken.None);
            Assert.Equal(cyrillic, Assert.Single(page.Items).Id);
        }

        // Beside a search that must still find nothing, so a LIKE that matched everything would fail here
        // rather than look like success.
        Assert.Equal(latin, Assert.Single(
            (await service.ListAsync(new IngestListQuery(Title: "barbie"), CancellationToken.None)).Items).Id);
        Assert.Empty((await service.ListAsync(new IngestListQuery(Title: "дюна"), CancellationToken.None)).Items);
    }

    [Fact]
    public async Task An_underscore_typed_by_the_caller_is_matched_literally()
    {
        // The other LIKE wildcard, and the one easy to forget: unescaped, "_" matches any character, so
        // a search for "The_Movie" would also return "The Movie" — a wrong row that looks plausible.
        using var harness = new PipelineTestHarness();
        var (underscored, catalogId, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "The_Movie.2021", "The_Movie.2021/movie.mkv");
        await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "The Movie.2021", "The Movie.2021/movie.mkv", catalogId);

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();

        var page = await service.ListAsync(new IngestListQuery(Title: "The_Movie"), CancellationToken.None);

        Assert.Equal(underscored, Assert.Single(page.Items).Id);
    }

    private static async Task SetStatusAsync(PipelineTestHarness harness, Guid ingestId, IngestStatus status) =>
        await MutateAsync(harness, ingestId, item => item.Status = status);

    private static async Task MutateAsync(PipelineTestHarness harness, Guid ingestId, Action<IngestItem> mutate)
    {
        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var item = await database.IngestItems.SingleAsync(entity => entity.Id == ingestId);
        mutate(item);
        await database.SaveChangesAsync();
    }

    private static async Task<Guid> AddMediaItemAsync(PipelineTestHarness harness, Guid catalogId, string title)
    {
        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var now = DateTimeOffset.UtcNow;
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalogId,
            Kind = MediaKind.Movie,
            Title = title,
            PublicId = Guid.NewGuid().ToString("N"),
            AddedAt = now,
            UpdatedAt = now,
        };
        database.MediaItems.Add(item);
        await database.SaveChangesAsync();
        return item.Id;
    }
}
