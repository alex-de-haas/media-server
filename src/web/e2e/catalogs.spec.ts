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
