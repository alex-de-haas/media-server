import { test, expect } from "@playwright/test";
import { setupApp } from "./support";

test("deduplicates duplicate review files and candidates", async ({ page }) => {
  const duplicatePath = ".incoming/57ae079cfcd94e8aa791a8c9c327a7e0/Zootopia.2.rus.LostFilm.TV.avi";
  const nestedDuplicatePath = `${duplicatePath}/Zootopia.2.rus.LostFilm.TV.avi`;
  const candidate = {
    reference: { provider: "tmdb", id: "1084242" },
    title: "Zootopia 2",
    year: 2025,
    score: 1,
  };
  const sourceFile = {
    relativePath: duplicatePath,
    sizeBytes: 1024,
    assignmentStatus: "NeedsReview",
    mediaItemId: null,
    parsedTitle: "Zootopia 2",
    parsedYear: null,
    parsedSeason: null,
    parsedEpisode: null,
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
        reviewCandidates: [candidate, candidate],
        sourceFiles: [
          { id: "source-1", ...sourceFile },
          { id: "source-2", ...sourceFile, relativePath: nestedDuplicatePath },
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
  await expect(dialog.getByText(duplicatePath, { exact: true })).toHaveCount(1);
  await expect(dialog.getByText(nestedDuplicatePath, { exact: true })).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: /Zootopia 2 \(2025\).*100%/ })).toHaveCount(1);
});
