import { test, expect, type Route } from "@playwright/test";
import { setupApp } from "./support";

// The Resolve-match dialog lists each unresolved file with its metadata candidates (poster + title + score).
// Duplicate source files are prevented at the backend (LocalTorrentInspector), so the dialog renders the
// files it is given as-is — it does not de-duplicate.
test("resolve match dialog shows the movie filename and metadata candidates", async ({ page }) => {
  const relativePath = ".incoming/57ae079cfcd94e8aa791a8c9c327a7e0/Zootopia.2.rus.LostFilm.TV.avi";
  const fileName = "Zootopia.2.rus.LostFilm.TV.avi";
  const candidate = {
    reference: { provider: "tmdb", id: "1084242" },
    title: "Zootopia 2",
    year: 2025,
    score: 1,
    posterUrl: null,
  };

  await setupApp(page, {
    catalogs: [{ id: "c1", name: "Movies", type: "Movie" }],
    ingest: [
      {
        id: "ingest-1",
        catalogId: "c1",
        downloadId: null,
        downloadName: "Zootopia.2.rus.LostFilm.TV.avi",
        mediaTitle: null,
        mediaItemId: null,
        stage: "Identify",
        status: "NeedsReview",
        attemptCount: 0,
        stagesCompleted: ["Intake", "Download"],
        lastError: null,
        nextAttemptAt: null,
        reviewCandidates: [candidate],
        sourceFiles: [
          {
            id: "source-1",
            relativePath,
            sizeBytes: 1024,
            assignmentStatus: "NeedsReview",
            mediaItemId: null,
            parsedTitle: "Zootopia 2",
            parsedYear: null,
            parsedSeason: null,
            parsedEpisode: null,
          },
        ],
        createdAt: "2026-06-24T10:00:00Z",
        updatedAt: "2026-06-24T10:00:00Z",
      },
    ],
  });

  await page.goto("/activity");
  await page.getByRole("button", { name: "Resolve match" }).click();

  const dialog = page.getByRole("dialog", { name: "Resolve match" });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByText(fileName, { exact: true }).last()).toBeVisible();
  await expect(dialog.getByText(relativePath, { exact: true })).toHaveCount(0);
  // A single-movie batch is one identity group, and a 100%-score candidate comes pre-selected.
  await expect(dialog.getByRole("button", { name: /Zootopia 2 \(2025\).*100%/ })).toHaveCount(1);
  await expect(dialog.getByRole("button", { name: /Zootopia 2 \(2025\).*100%/ })).toHaveAttribute("aria-pressed", "true");

  // Apply sends one match request; movie catalogs always use the grouped shape, here a single group.
  const matchRequest = page.waitForRequest(
    (request) => request.url().includes("/ingest/ingest-1/match") && request.method() === "POST",
  );
  await dialog.getByRole("button", { name: "Approve (1)" }).click();
  const body = (await matchRequest).postDataJSON() as {
    groups: { kind: string; providerId: string; files: { sourceFileId: string }[] }[];
  };
  expect(body.groups).toHaveLength(1);
  expect(body.groups[0].kind).toBe("Movie");
  expect(body.groups[0].providerId).toBe("1084242");
  expect(body.groups[0].files.map((file) => file.sourceFileId)).toEqual(["source-1"]);
});

// A franchise pack holds several movies, so the movie-catalog dialog groups the files by parsed title and
// confirms one identity per group — the whole batch resolving in a single grouped match request.
test("resolve match dialog maps a franchise pack to one movie per group", async ({ page }) => {
  const dieHard = { reference: { provider: "tmdb", id: "562" }, title: "Die Hard", year: 1988, score: 0.6, posterUrl: null };
  const dieHard2 = { reference: { provider: "tmdb", id: "1573" }, title: "Die Hard 2", year: 1990, score: 0.6, posterUrl: null };

  await setupApp(page, {
    catalogs: [{ id: "c1", name: "Movies", type: "Movie" }],
    // Both groups search against the same mocked provider results and pick different candidates from them.
    metadataSearch: [dieHard, dieHard2],
    ingest: [
      {
        id: "ingest-1",
        catalogId: "c1",
        downloadId: null,
        downloadName: "Die.Hard.Quadrilogy",
        mediaTitle: null,
        mediaItemId: null,
        stage: "Identify",
        status: "NeedsReview",
        attemptCount: 0,
        stagesCompleted: ["Intake", "Download"],
        lastError: null,
        nextAttemptAt: null,
        reviewCandidates: [],
        sourceFiles: [
          {
            id: "source-1",
            relativePath: "Die.Hard.Quadrilogy/Die.Hard.1988.mkv",
            sizeBytes: 1024,
            assignmentStatus: "NeedsReview",
            mediaItemId: null,
            parsedTitle: "Die Hard",
            parsedYear: 1988,
            parsedSeason: null,
            parsedEpisode: null,
          },
          {
            id: "source-2",
            relativePath: "Die.Hard.Quadrilogy/Die.Hard.2.1990.mkv",
            sizeBytes: 1024,
            assignmentStatus: "NeedsReview",
            mediaItemId: null,
            parsedTitle: "Die Hard 2",
            parsedYear: 1990,
            parsedSeason: null,
            parsedEpisode: null,
          },
        ],
        createdAt: "2026-07-24T10:00:00Z",
        updatedAt: "2026-07-24T10:00:00Z",
      },
    ],
  });

  await page.goto("/activity");
  await page.getByRole("button", { name: "Resolve match" }).click();

  const dialog = page.getByRole("dialog", { name: "Resolve match" });
  await expect(dialog).toBeVisible();
  // Two files with different parsed titles pre-group into two movies, each awaiting its own pick.
  await expect(dialog.getByText("Movie 1 — pick below")).toBeVisible();
  await expect(dialog.getByText("Movie 2 — pick below")).toBeVisible();

  // Each group renders the same mocked candidates, so the group's own card decides which is which:
  // the first group's list picks Die Hard, the second group's picks Die Hard 2.
  await dialog.getByRole("button", { name: /Die Hard \(1988\)/ }).first().click();
  await dialog.getByRole("button", { name: /Die Hard 2 \(1990\)/ }).last().click();

  const matchRequest = page.waitForRequest(
    (request) => request.url().includes("/ingest/ingest-1/match") && request.method() === "POST",
  );
  await dialog.getByRole("button", { name: "Approve (2)" }).click();
  const body = (await matchRequest).postDataJSON() as {
    groups: { kind: string; providerId: string; title: string; files: { sourceFileId: string }[] }[];
  };

  // One request, one group per movie, each carrying only its own file.
  expect(body.groups).toHaveLength(2);
  expect(body.groups.map((group) => group.providerId)).toEqual(["562", "1573"]);
  expect(body.groups.map((group) => group.files.map((file) => file.sourceFileId))).toEqual([["source-1"], ["source-2"]]);
  expect(body.groups.every((group) => group.kind === "Movie")).toBe(true);
});

// A group auto-searches when the dialog opens, and re-opening the dialog re-seeds the same group with a
// second search. If the first response arrives last it would replace the newer candidates, so the
// operator could pick a movie from a list that no longer belongs to the group on screen.
test("a superseded group search cannot overwrite newer candidates", async ({ page }) => {
  const stale = { reference: { provider: "tmdb", id: "1" }, title: "Stale Result", year: 1999, score: 0.4, posterUrl: null };
  const fresh = { reference: { provider: "tmdb", id: "562" }, title: "Die Hard", year: 1988, score: 0.9, posterUrl: null };

  await setupApp(page, {
    catalogs: [{ id: "c1", name: "Movies", type: "Movie" }],
    ingest: [
      {
        id: "ingest-1",
        catalogId: "c1",
        downloadId: null,
        downloadName: "Dye.Haard.1988.mkv",
        mediaTitle: null,
        mediaItemId: null,
        stage: "Identify",
        status: "NeedsReview",
        attemptCount: 0,
        stagesCompleted: ["Intake", "Download"],
        lastError: null,
        nextAttemptAt: null,
        reviewCandidates: [],
        sourceFiles: [
          {
            id: "source-1",
            relativePath: "Dye.Haard.1988.mkv",
            sizeBytes: 1024,
            assignmentStatus: "NeedsReview",
            mediaItemId: null,
            parsedTitle: "Dye Haard",
            parsedYear: 1988,
            parsedSeason: null,
            parsedEpisode: null,
          },
        ],
        createdAt: "2026-07-24T10:00:00Z",
        updatedAt: "2026-07-24T10:00:00Z",
      },
    ],
  });

  // Registered after setupApp so it takes precedence. The first search is captured and left unanswered
  // rather than delayed inside the handler: Playwright runs route handlers one at a time, so awaiting in
  // here would hold back the second request too and there would be no overlap to test. The held response
  // is released by hand at the end — exactly the out-of-order arrival the guard exists for.
  let heldFirstSearch: Route | null = null;
  await page.route("**/api/proxy/api/ingest/*/search", async (route) => {
    if (heldFirstSearch === null) {
      heldFirstSearch = route;
      return;
    }
    await route.fulfill({ json: [fresh] });
  });

  await page.goto("/activity");
  await page.getByRole("button", { name: "Resolve match" }).click();

  const dialog = page.getByRole("dialog", { name: "Resolve match" });
  await expect(dialog).toBeVisible();
  await expect.poll(() => heldFirstSearch !== null).toBe(true);

  // Close and re-open while that search is still in flight: the group is re-seeded and searches again,
  // so two responses are now racing for the same group.
  await dialog.getByRole("button", { name: "Close" }).click();
  await expect(dialog).toBeHidden();
  await page.getByRole("button", { name: "Resolve match" }).click();
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole("button", { name: /Die Hard \(1988\)/ })).toBeVisible();

  // Release the superseded response now, and wait for the browser to have it in hand.
  const staleReceived = page.waitForResponse((response) => response.url().includes("/ingest/ingest-1/search"));
  await heldFirstSearch!.fulfill({ json: [stale] });
  await staleReceived;

  // Give React a frame to apply it if it were going to, then check the newer candidates (and the group's
  // settled spinner) survived untouched.
  await page.waitForTimeout(300);
  await expect(dialog.getByRole("button", { name: /Die Hard \(1988\)/ })).toBeVisible();
  await expect(dialog.getByText("Stale Result", { exact: false })).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: "Search" })).toBeEnabled();
});

test("resolve match dialog shows full filenames for every episode in a series pack", async ({ page }) => {
  const firstFileName = "Fullmetal Alchemist Brotherhood - 01 - Fullmetal Alchemist [1080p].mkv";
  const secondFileName = "Fullmetal Alchemist Brotherhood - 02 - The First Day [1080p].mkv";
  const stagingRoot = ".incoming/47d8d8cd10ee4fd681f4afcb30796b59/Fullmetal Alchemist Brotherhood";
  const candidate = {
    reference: { provider: "tmdb", id: "31911" },
    title: "Fullmetal Alchemist: Brotherhood",
    year: 2009,
    score: 1,
    posterUrl: null,
  };

  await setupApp(page, {
    catalogs: [{ id: "c1", name: "Series", type: "Series" }],
    ingest: [
      {
        id: "ingest-1",
        catalogId: "c1",
        downloadId: null,
        downloadName: "Fullmetal Alchemist Brotherhood",
        mediaTitle: null,
        mediaItemId: null,
        stage: "Identify",
        status: "NeedsReview",
        attemptCount: 0,
        stagesCompleted: ["Intake", "Download"],
        lastError: null,
        nextAttemptAt: null,
        reviewCandidates: [candidate],
        sourceFiles: [
          {
            id: "source-1",
            relativePath: `${stagingRoot}/${firstFileName}`,
            sizeBytes: 1024,
            assignmentStatus: "NeedsReview",
            mediaItemId: null,
            parsedTitle: "Fullmetal Alchemist Brotherhood",
            parsedYear: null,
            parsedSeason: 1,
            parsedEpisode: 1,
          },
          {
            id: "source-2",
            relativePath: `${stagingRoot}/${secondFileName}`,
            sizeBytes: 1024,
            assignmentStatus: "NeedsReview",
            mediaItemId: null,
            parsedTitle: "Fullmetal Alchemist Brotherhood",
            parsedYear: null,
            parsedSeason: 1,
            parsedEpisode: 2,
          },
        ],
        createdAt: "2026-06-24T10:00:00Z",
        updatedAt: "2026-06-24T10:00:00Z",
      },
    ],
  });

  await page.goto("/activity");
  await page.getByRole("button", { name: "Resolve match" }).click();

  const dialog = page.getByRole("dialog", { name: "Resolve match" });
  await expect(dialog.getByText(firstFileName, { exact: true })).toBeVisible();
  await expect(dialog.getByText(secondFileName, { exact: true })).toBeVisible();
  await expect(dialog.getByText(stagingRoot, { exact: false })).toHaveCount(0);
  // The series candidate appears once at the top, not per file.
  await expect(dialog.getByRole("button", { name: /Fullmetal Alchemist: Brotherhood \(2009\).*100%/ })).toHaveCount(1);
  await expect(dialog.getByLabel("Episode").nth(0)).toHaveValue("1");
  await expect(dialog.getByLabel("Episode").nth(1)).toHaveValue("2");
});

// Already-mapped files stay visible with their current mapping (verifiable by the operator) and can be
// re-decided via Change while the batch is still in review; the series is confirmed once for the batch.
test("resolve match dialog shows mapped files and lets the operator re-decide them", async ({ page }) => {
  const mappedFileName = "Fullmetal Alchemist Brotherhood - 01 [1080p].mkv";
  const extraFileName = "Fullmetal Alchemist Brotherhood (Creditless ED 1) [1080p].mkv";
  const stagingRoot = ".incoming/47d8d8cd10ee4fd681f4afcb30796b59/Fullmetal Alchemist Brotherhood";
  const candidate = {
    reference: { provider: "tmdb", id: "31911" },
    title: "Fullmetal Alchemist: Brotherhood",
    year: 2009,
    score: 1,
    posterUrl: null,
  };

  await setupApp(page, {
    catalogs: [{ id: "c1", name: "Series", type: "Series" }],
    ingest: [
      {
        id: "ingest-1",
        catalogId: "c1",
        downloadId: null,
        downloadName: "Fullmetal Alchemist Brotherhood",
        mediaTitle: null,
        mediaItemId: null,
        stage: "Identify",
        status: "NeedsReview",
        attemptCount: 0,
        stagesCompleted: ["Intake", "Download"],
        lastError: null,
        nextAttemptAt: null,
        reviewCandidates: [candidate],
        sourceFiles: [
          {
            id: "source-1",
            relativePath: `${stagingRoot}/${mappedFileName}`,
            sizeBytes: 1024,
            assignmentStatus: "Confirmed",
            mediaItemId: "m1",
            assigned: {
              kind: "Episode",
              title: "Episode 1",
              season: 1,
              episode: 1,
              seriesTitle: "Fullmetal Alchemist: Brotherhood",
              provider: "tmdb",
              providerId: "31911",
            },
            parsedTitle: "Fullmetal Alchemist Brotherhood",
            parsedYear: null,
            parsedSeason: 1,
            parsedEpisode: 1,
          },
          {
            id: "source-2",
            relativePath: `${stagingRoot}/${extraFileName}`,
            sizeBytes: 1024,
            assignmentStatus: "NeedsReview",
            mediaItemId: null,
            assigned: null,
            parsedTitle: "Fullmetal Alchemist Brotherhood",
            parsedYear: null,
            parsedSeason: null,
            parsedEpisode: null,
            extraKind: "CreditlessEnding",
            extraTitle: "Creditless Ending 1",
            extraSuggestSkip: false,
          },
        ],
        createdAt: "2026-06-24T10:00:00Z",
        updatedAt: "2026-06-24T10:00:00Z",
      },
    ],
  });

  await page.goto("/activity");
  await page.getByRole("button", { name: "Resolve match" }).click();
  const dialog = page.getByRole("dialog", { name: "Resolve match" });

  // The mapped file is visible with its current mapping, and the series it resolved against is the
  // pre-selected identity for the whole batch.
  await expect(dialog.getByText("Already mapped (1)", { exact: false })).toBeVisible();
  await expect(dialog.getByText("S01E01", { exact: true })).toBeVisible();
  await expect(dialog.getByRole("button", { name: /Fullmetal Alchemist: Brotherhood.*Current match/ })).toHaveAttribute(
    "aria-pressed",
    "true",
  );

  // The classified extra pre-selects the Extra decision; only it counts as a pending change.
  await expect(dialog.getByRole("button", { name: "Extra", exact: true })).toHaveAttribute("aria-pressed", "true");
  await expect(dialog.getByRole("button", { name: "Approve (1)" })).toBeEnabled();

  // Change flips the mapped file into an editable decision, seeded from its current mapping.
  await dialog.getByRole("button", { name: "Change" }).click();
  await expect(dialog.getByLabel("Episode")).toHaveValue("1");
  await expect(dialog.getByRole("button", { name: "Approve (2)" })).toBeEnabled();
});
