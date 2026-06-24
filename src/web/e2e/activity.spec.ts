import { test, expect } from "@playwright/test";
import { setupApp } from "./support";

// The Resolve-match dialog lists each unresolved file with its metadata candidates (poster + title + score).
// Duplicate source files are prevented at the backend (LocalTorrentInspector), so the dialog renders the
// files it is given as-is — it does not de-duplicate.
test("resolve match dialog lists metadata candidates for a needs-review item", async ({ page }) => {
  const relativePath = ".incoming/57ae079cfcd94e8aa791a8c9c327a7e0/Zootopia.2.rus.LostFilm.TV.avi";
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
  await expect(dialog.getByText(relativePath, { exact: true })).toHaveCount(1);
  await expect(dialog.getByRole("button", { name: /Zootopia 2 \(2025\).*100%/ })).toHaveCount(1);
});
