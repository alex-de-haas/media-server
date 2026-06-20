import { test, expect } from "@playwright/test";
import { aMovie, movieDetail, setupApp } from "./support";

test("opens a movie detail page and marks it watched", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Arrival")],
    detail: { m1: movieDetail("m1", "Arrival") },
  });

  await page.goto("/movies");
  await page.getByRole("link", { name: /Arrival/ }).click();
  await expect(page).toHaveURL(/\/movies\/m1$/);
  await expect(page.getByRole("heading", { name: "Arrival" })).toBeVisible();

  const played = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/m1/played") && request.method() === "POST",
  );
  await page.getByRole("button", { name: "Mark watched" }).click();
  await played;
});

test("plays a movie through an Infuse deep link", async ({ page }) => {
  await setupApp(page, {
    library: [aMovie("m1", "Arrival")],
    detail: { m1: movieDetail("m1", "Arrival", "329865") },
  });

  // Capture the deep link the page launches (window.open) instead of actually following the custom scheme.
  await page.addInitScript(() => {
    (window as unknown as { __infuse: string[] }).__infuse = [];
    window.open = ((url?: string | URL) => {
      (window as unknown as { __infuse: string[] }).__infuse.push(String(url));
      return null;
    }) as typeof window.open;
  });

  await page.goto("/movies/m1");
  await page.getByRole("button", { name: "Play in Infuse" }).click();

  const opened = await page.evaluate(() => (window as unknown as { __infuse: string[] }).__infuse);
  expect(opened).toContain("infuse://movie/329865?play");
});

test("admin fixes a misidentified movie and lands on the corrected item", async ({ page }) => {
  await setupApp(page, {
    role: "admin",
    library: [aMovie("m1", "Wrong Title")],
    detail: { m1: movieDetail("m1", "Wrong Title"), m2: movieDetail("m2", "Arrival") },
    metadataSearch: [{ reference: { provider: "tmdb", id: "329865" }, title: "Arrival", year: 2016, score: 1 }],
    remapTargetId: "m2",
  });

  await page.goto("/movies/m1");
  await page.getByRole("button", { name: "More actions" }).click();
  await page.getByRole("menuitem", { name: /Fix match/ }).click();

  await page.getByRole("textbox", { name: "Movie title" }).fill("Arrival");
  await page.getByRole("button", { name: /Search/ }).click();

  const remapped = page.waitForRequest(
    (request) => request.url().includes("/api/proxy/api/library/m1/remap") && request.method() === "POST",
  );
  await page.getByRole("button", { name: /Arrival \(2016\)/ }).click();
  await remapped;

  await expect(page).toHaveURL(/\/movies\/m2$/);
  await expect(page.getByRole("heading", { name: "Arrival" })).toBeVisible();
});
