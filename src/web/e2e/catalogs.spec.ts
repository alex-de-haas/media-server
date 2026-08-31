import { expect, test } from "@playwright/test";
import { aCatalog, setupApp } from "./support";

const MOVIES = "11111111-1111-1111-1111-111111111111";

const MOUNTS = [
  { label: "dev_media_1", path: "/mnt/catalogRoots/dev_media_1" },
  { label: "dev_media_2", path: "/mnt/catalogRoots/dev_media_2" },
];

test("an unanchored catalog is labelled for re-anchoring, not as an offline volume", async ({ page }) => {
  await setupApp(page, {
    catalogs: [
      aCatalog(MOVIES, "Movies", "Movie", false, {
        root: "/Users/haas/dev-media/movies",
        mountLabel: "dev_media_1",
        mountRelativePath: "movies",
        unanchored: true,
      }),
    ],
    catalogMounts: MOUNTS,
  });

  await page.goto("/catalogs");

  // The distinction matters: "offline" tells the operator to reconnect a volume, which would be wrong.
  await expect(page.getByText("unanchored")).toBeVisible();
  await expect(page.getByText("offline")).toHaveCount(0);
});

test("re-anchoring a catalog whose mount is gone submits the mount it actually shows", async ({ page }) => {
  await setupApp(page, {
    catalogs: [
      aCatalog(MOVIES, "Movies", "Movie", false, {
        root: "/Users/haas/dev-media/movies",
        mountLabel: "retired_mount", // No longer configured — this is why the catalog is unanchored.
        mountRelativePath: "movies",
        unanchored: true,
      }),
    ],
    catalogMounts: MOUNTS,
  });

  await page.goto("/catalogs");
  await page.getByRole("button", { name: "Catalog actions" }).click();
  await page.getByRole("menuitem", { name: "Re-anchor" }).click();

  // The picker falls back to the first configured mount; submitting the dead label would just 400.
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("combobox", { name: "Mount" })).toContainText("dev_media_1");

  const request = page.waitForRequest((candidate) =>
    candidate.url().endsWith(`/api/proxy/api/catalogs/${MOVIES}/anchor`) && candidate.method() === "POST",
  );
  await dialog.getByRole("button", { name: "Re-anchor" }).click();

  expect((await request).postDataJSON()).toEqual({ mountLabel: "dev_media_1", relativePath: "movies" });
});

test("re-anchoring sends the mount label and a normalized path within it", async ({ page }) => {
  await setupApp(page, {
    catalogs: [
      aCatalog(MOVIES, "Movies", "Movie", false, {
        root: "/Users/haas/dev-media/movies",
        mountLabel: "dev_media_1",
        mountRelativePath: "movies",
        unanchored: true,
      }),
    ],
    catalogMounts: MOUNTS,
  });

  await page.goto("/catalogs");
  await page.getByRole("button", { name: "Catalog actions" }).click();
  await page.getByRole("menuitem", { name: "Re-anchor" }).click();

  // Opens on the catalog's current anchor.
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("combobox", { name: "Mount" })).toContainText("dev_media_1");
  await expect(dialog.getByRole("textbox", { name: "Path within mount" })).toHaveValue("movies");

  await dialog.getByRole("combobox", { name: "Mount" }).click();
  await page.getByRole("option", { name: "dev_media_2" }).click();
  await dialog.getByRole("textbox", { name: "Path within mount" }).fill("/films/");
  // The resolved root for this runtime is shown before committing.
  await expect(dialog.getByText("/mnt/catalogRoots/dev_media_2/films")).toBeVisible();

  const request = page.waitForRequest((candidate) =>
    candidate.url().endsWith(`/api/proxy/api/catalogs/${MOVIES}/anchor`) && candidate.method() === "POST",
  );
  await dialog.getByRole("button", { name: "Re-anchor" }).click();

  // The label is what identifies the mount across runtimes; the sub-path is normalized before sending.
  expect((await request).postDataJSON()).toEqual({ mountLabel: "dev_media_2", relativePath: "films" });
  await expect(page.getByText("Catalog re-anchored")).toBeVisible();
});

test("scanning a catalog reports what left the library, not just what arrived", async ({ page }) => {
  // Both halves of a scan matter to an operator, and the removal half is the one that has to be said
  // out loud: a title disappearing without a word is what reads as data loss.
  await setupApp(page, {
    catalogs: [aCatalog(MOVIES, "Movies", "Movie", true)],
    catalogScan: {
      catalogId: MOVIES,
      catalogName: "Movies",
      offline: false,
      filesScanned: 12,
      imported: 2,
      skipped: 10,
      sourcesChecked: 40,
      missingFiles: 3,
      versionsRemoved: 1,
      sidecarsRemoved: 0,
      titlesGhosted: 1,
      titlesPurged: 1,
      missingPaths: [],
    },
  });

  await page.goto("/catalogs");
  await page.getByRole("button", { name: "Catalog actions" }).click();
  await page.getByRole("menuitem", { name: "Scan for media" }).click();

  await expect(page.getByText("3 files gone from disk")).toBeVisible();
  await expect(page.getByText(/1 title left the library but kept their watch history/)).toBeVisible();
  await expect(page.getByText(/1 title nobody had watched were deleted/)).toBeVisible();
});

test("a catalog whose volume is gone is reported as offline, not as an emptied library", async ({ page }) => {
  await setupApp(page, {
    catalogs: [aCatalog(MOVIES, "Movies", "Movie", true)],
    catalogScan: {
      catalogId: MOVIES,
      catalogName: "Movies",
      offline: true,
      filesScanned: 0,
      imported: 0,
      skipped: 0,
      sourcesChecked: 0,
      missingFiles: 0,
      versionsRemoved: 0,
      sidecarsRemoved: 0,
      titlesGhosted: 0,
      titlesPurged: 0,
      missingPaths: [],
    },
  });

  await page.goto("/catalogs");
  await page.getByRole("button", { name: "Catalog actions" }).click();
  await page.getByRole("menuitem", { name: "Scan for media" }).click();

  await expect(page.getByText("Movies is offline")).toBeVisible();
  await expect(page.getByText(/Reconnect the volume and scan again/)).toBeVisible();
});

test("the whole library can be scanned from one button", async ({ page }) => {
  await setupApp(page, {
    catalogs: [aCatalog(MOVIES, "Movies", "Movie", true)],
    libraryScan: {
      catalogs: [],
      catalogsScanned: 2,
      catalogsOffline: 1,
      imported: 0,
      sourcesChecked: 500,
      missingFiles: 0,
      versionsRemoved: 0,
      sidecarsRemoved: 0,
      titlesGhosted: 0,
      titlesPurged: 0,
    },
  });

  await page.goto("/catalogs");

  const posted = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/catalogs/scan") && request.method() === "POST",
  );
  await page.getByRole("button", { name: "Scan all" }).click();
  await posted;

  await expect(page.getByText("Library is in sync")).toBeVisible();
  await expect(page.getByText(/1 catalog offline and left alone/)).toBeVisible();
});
