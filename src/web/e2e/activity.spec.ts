import { test, expect } from "@playwright/test";
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
  await expect(dialog.getByRole("button", { name: /Zootopia 2 \(2025\).*100%/ })).toHaveCount(1);
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
  await expect(dialog.getByLabel("Episode").nth(0)).toHaveValue("1");
  await expect(dialog.getByLabel("Episode").nth(1)).toHaveValue("2");
});
