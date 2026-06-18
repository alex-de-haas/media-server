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
