import { expect, test } from "@playwright/test";
import { anEpisode, aSeason, aUserData, movieDetail, seriesDetail, setupApp } from "./support";

const lastWatch = "2026-07-22T18:00:00Z";

for (const action of ["finish", "dismiss"] as const) {
  test(`movie resume can ${action} without changing the meaning of the other action`, async ({ page }) => {
    const data = aUserData({ playbackPositionTicks: 10000000, playedPercentage: 70, lastWatchedAt: lastWatch, playCount: 1 });
    let called = 0;
    await setupApp(page, { detail: { m1: { ...movieDetail("m1", "Arrival"), userData: data } } });
    await page.route(`**/api/proxy/api/library/m1/resume${action === "finish" ? "/finish" : ""}`, async (route) => {
      expect(route.request().method()).toBe(action === "finish" ? "POST" : "DELETE");
      called++;
      data.playbackPositionTicks = 0;
      if (action === "finish") { data.played = true; data.playCount++; }
      await route.fulfill({ json: data });
    });
    await page.goto("/movies/m1");
    await expect(page.getByLabel("Watch status")).toContainText("Jul 22, 2026");
    await expect(page.getByRole("button", { name: /^Mark (un)?watched$/ })).toHaveCount(0);
    await page.getByRole("button", { name: action === "finish" ? "Finish watching Arrival" : "Remove Arrival from Continue watching" }).click();
    await expect(page.getByRole("button", { name: "Finish watching Arrival" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Log watch", exact: true })).toBeVisible();
    expect(called).toBe(1);
    expect(data.playCount).toBe(action === "finish" ? 2 : 1);
  });
}

for (const count of [0, 1, 2]) {
  test(`a movie with ${count} watches has the appropriate status and timeline`, async ({ page }) => {
    await setupApp(page, { detail: { m1: { ...movieDetail("m1", "Arrival"), userData: aUserData({ played: count > 0, lastWatchedAt: count ? lastWatch : null }) } } });
    await page.route("**/api/proxy/api/library/m1/watch-history?*", (route) => route.fulfill({ json: {
      entries: Array.from({ length: count }, (_, index) => ({ id: `e${index}`, watchedAt: lastWatch })),
      undated: [], total: count, datedTotal: count, offset: 0, limit: 20,
    } }));
    await page.goto("/movies/m1");
    await expect(page.getByLabel("Watch status")).toContainText(count ? "Jul 22, 2026" : "Not watched");
    const history = page.getByRole("region", { name: "Watch history" });
    if (count < 2) await expect(history).toHaveCount(0);
    else await expect(history.getByRole("listitem")).toHaveCount(2);
  });
}

test("clearing progress removes a card from the home resume rail without navigation", async ({ page }) => {
  let cleared = false;
  const card = { id: "m1", title: "Arrival", subtitle: null, posterUrl: null, navigationKind: "Movie", navigationId: "m1", userData: aUserData({ playbackPositionTicks: 10000000 }) };
  await setupApp(page);
  await page.route("**/api/proxy/api/library/resume", (route) => route.fulfill({ json: cleared ? [] : [card] }));
  await page.route("**/api/proxy/api/library/m1/resume", (route) => {
    cleared = true;
    return route.fulfill({ json: aUserData() });
  });
  await page.goto("/");
  await page.getByRole("button", { name: "Remove Arrival from Continue watching" }).click();
  await expect(page.getByRole("heading", { name: "Continue watching" })).toHaveCount(0);
  await expect(page).toHaveURL("/");
});

test("episode completion targets the episode and its controls fit a phone", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const data = aUserData({ playbackPositionTicks: 10000000 });
  const episode = { ...anEpisode("e1", 1, 1, "First episode"), userData: data };
  await setupApp(page, {
    detail: { s1: { ...seriesDetail("s1", "Severance"), seasons: [aSeason("season-1", 1, 1)] } },
    episodes: { s1: [episode] },
  });
  let completed = false;
  await page.route("**/api/proxy/api/library/e1/resume/finish", (route) => {
    completed = true;
    data.playbackPositionTicks = 0;
    data.played = true;
    return route.fulfill({ json: data });
  });
  await page.goto("/series/s1");
  await page.getByRole("button", { name: /^Season 1 ·/ }).click();
  const row = page.getByRole("listitem").filter({ hasText: "First episode" });
  const finish = row.getByRole("button", { name: /^Finish watching/ });
  const box = await finish.boundingBox();
  expect(box).not.toBeNull();
  expect(box!.x + box!.width).toBeLessThanOrEqual(390);
  await finish.click();
  await expect(row.getByRole("button", { name: "Log watch", exact: true })).toBeVisible();
  expect(completed).toBe(true);
});

test("a failed completion preserves the resume actions for retry", async ({ page }) => {
  await setupApp(page, { detail: { m1: { ...movieDetail("m1", "Arrival"), userData: aUserData({ playbackPositionTicks: 10000000 }) } } });
  await page.route("**/api/proxy/api/library/m1/resume/finish", (route) => route.fulfill({ status: 500, json: { error: "Unable to save" } }));
  await page.goto("/movies/m1");
  await page.getByRole("button", { name: "Finish watching Arrival" }).click();
  await expect(page.getByRole("button", { name: "Finish watching Arrival" })).toBeEnabled();
  await expect(page.getByRole("button", { name: "Remove Arrival from Continue watching" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Log watch", exact: true })).toHaveCount(0);
});
