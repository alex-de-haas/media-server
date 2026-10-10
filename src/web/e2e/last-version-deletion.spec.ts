import { test, expect, type Page } from "@playwright/test";
import { anEpisode, episodeDetail, movieDetail, seriesDetail, setupApp } from "./support";

const source = (id: string, sourceKind = "File") => ({
  id, versionName: id, fileName: `${id}.mkv`, container: sourceKind === "Bluray" ? "bluray" : "mkv",
  sourceKind, sizeBytes: 1024, durationTicks: 60_000_000, bitrate: null, streams: [],
});

type Kind = "movie" | "episode";

async function openMedia(page: Page, kind: Kind, sources = [source("original")], role: "admin" | "user" = "admin") {
  const detail = { ...(kind === "movie" ? movieDetail("item", "Arrival") : episodeDetail("item", "Pilot")), mediaSources: sources, defaultSourceId: "original" };
  await setupApp(page, {
    role,
    detail: { item: detail, series: seriesDetail("series", "Severance") },
    episodes: { series: [anEpisode("item", 1, 1, "Pilot")] },
  });
  await page.goto(kind === "movie" ? "/movies/item" : "/series/series");
  if (kind === "episode") {
    await page.getByRole("button", { name: /^Season 1 ·/ }).click();
    await page.getByRole("button", { name: "Show media of S01E01" }).click();
  }
  await expect(page.getByText("original.mkv", { exact: true })).toBeVisible();
  return detail;
}

for (const kind of ["movie", "episode"] as const) {
  for (const erase of [false, true]) {
    test(`${kind} last version opens item deletion with safe defaults and ${erase ? "explicit erasure" : "preservation"}`, async ({ page }) => {
      const deletes: string[] = [];
      page.on("request", request => { if (request.method() === "DELETE") deletes.push(request.url()); });
      await openMedia(page, kind);
      await expect(page.getByRole("button", { name: "Delete this version" })).toHaveCount(0);
      await page.getByRole("button", { name: `Delete ${kind}`, exact: true }).click();
      const dialog = page.getByRole("alertdialog");
      await expect(dialog.getByRole("heading", { name: `Delete ${kind}?` })).toBeVisible();
      await expect(dialog.getByText(/This is the last version/)).toBeVisible();
      await expect(dialog.getByRole("checkbox", { name: /^Delete files from disk/ })).not.toBeChecked();
      await expect(dialog.getByRole("checkbox", { name: /^Also delete watch history and favorites/ })).not.toBeChecked();
      await dialog.getByRole("checkbox", { name: /^Delete files from disk/ }).check();
      await dialog.getByRole("checkbox", { name: /^Also delete watch history and favorites/ }).check();
      await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
      await expect(dialog).toBeHidden();
      expect(deletes).toEqual([]);

      await page.getByRole("button", { name: `Delete ${kind}`, exact: true }).click();
      await expect(dialog.getByRole("checkbox", { name: /^Delete files from disk/ })).not.toBeChecked();
      await expect(dialog.getByRole("checkbox", { name: /^Also delete watch history and favorites/ })).not.toBeChecked();
      if (erase) {
        await dialog.getByRole("checkbox", { name: /^Delete files from disk/ }).check();
        await dialog.getByRole("checkbox", { name: /^Also delete watch history and favorites/ }).check();
      }
      const removed = page.waitForRequest(request => request.method() === "DELETE");
      await dialog.getByRole("button", { name: erase ? "Delete + remove files" : "Remove from library", exact: true }).click();
      const url = new URL((await removed).url());
      expect(url.pathname).toBe(`/api/proxy/api/library/${kind === "episode" ? "episodes/" : ""}item`);
      expect(url.searchParams.get("deleteFiles")).toBe(String(erase));
      expect(url.searchParams.get("deleteUserData")).toBe(String(erase));
      expect(deletes).toHaveLength(1);
    });
  }

  test(`${kind} with several versions still removes just one version`, async ({ page }) => {
    await openMedia(page, kind, [source("original"), source("replacement")]);
    await page.getByRole("button", { name: "Delete this version" }).first().click();
    const dialog = page.getByRole("alertdialog");
    await expect(dialog.getByRole("heading", { name: "Remove this version?" })).toBeVisible();
    await expect(dialog.getByRole("checkbox", { name: /^Also delete watch history and favorites/ })).toHaveCount(0);
    const removed = page.waitForRequest(request => request.method() === "DELETE");
    await dialog.getByRole("button", { name: "Remove version", exact: true }).click();
    const url = new URL((await removed).url());
    expect(url.pathname).toBe("/api/proxy/api/library/sources/original");
    expect(url.searchParams.get("deleteFile")).toBe("false");
  });

  test(`${kind} stale last-version conflict opens item confirmation without deleting the item`, async ({ page }) => {
    const deletes: string[] = [];
    page.on("request", request => { if (request.method() === "DELETE") deletes.push(request.url()); });
    const detail = await openMedia(page, kind, [source("original"), source("replacement")]);
    // Another browser removes the replacement after this screen loaded.
    let refreshes = 0;
    await page.route("**/api/proxy/api/library/item", route => {
      refreshes++;
      return route.fulfill({ json: { ...detail, mediaSources: [source("original")] } });
    });
    await page.route("**/api/proxy/api/library/sources/original*", route => route.fulfill({
      status: 409, json: { error: "last_media_source", detail: "Delete the movie or episode instead." },
    }));
    await page.getByRole("button", { name: "Delete this version" }).first().click();
    await page.getByRole("alertdialog").getByRole("checkbox", { name: /^Delete file from disk/ }).check();
    await page.getByRole("alertdialog").getByRole("button", { name: "Delete + remove file", exact: true }).click();
    const dialog = page.getByRole("alertdialog");
    await expect(dialog.getByRole("heading", { name: `Delete ${kind}?` })).toBeVisible();
    await expect(dialog.getByText(/This is the last version/)).toBeVisible();
    await expect(dialog.getByRole("checkbox", { name: /^Delete files from disk/ })).not.toBeChecked();
    await expect(dialog.getByRole("checkbox", { name: /^Also delete watch history and favorites/ })).not.toBeChecked();
    await expect.poll(() => refreshes).toBeGreaterThan(0);
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(dialog).toBeHidden();
    expect(deletes).toHaveLength(1);
    expect(new URL(deletes[0]).pathname).toBe("/api/proxy/api/library/sources/original");
  });

  test(`a viewer has no last-version ${kind} deletion control`, async ({ page }) => {
    await openMedia(page, kind, [source("original")], "user");
    await expect(page.getByRole("button", { name: `Delete ${kind}`, exact: true })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Delete this version" })).toHaveCount(0);
  });
}

test("an MKV beside a Blu-ray disc is not the last version", async ({ page }) => {
  await openMedia(page, "movie", [source("original"), source("disc", "Bluray")]);
  await expect(page.getByRole("button", { name: "Delete this version" })).toHaveCount(2);
  await page.getByRole("button", { name: "Delete this version" }).first().click();
  await expect(page.getByRole("alertdialog").getByRole("heading", { name: "Remove this version?" })).toBeVisible();
});
