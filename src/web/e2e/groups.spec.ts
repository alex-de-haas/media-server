import { expect, test, type Page } from "@playwright/test";
import { setupApp } from "./support";
import type { GroupInput } from "../src/lib/groups";

const movie = { id: "movie-one", title: "Retro Film", year: 1999, kind: "Movie", catalogId: "movies", posterUrl: null, userData: null };
const series = { ...movie, id: "series-one", title: "Retro Series", kind: "Series", catalogId: "series" };
const manual: GroupInput = { name: "Weekend", kind: "manual", catalogType: "movie", rules: { version: 1, match: "all", conditions: [] }, memberIds: [movie.id] };

async function mockGroups(page: Page, initial: Record<string, GroupInput> = {}, role: "admin" | "user" = "admin") {
  await setupApp(page, { role });
  const groups = { ...initial };
  const writes: GroupInput[] = [];
  await page.route("**/api/proxy/api/groups**", async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname.replace("/api/proxy/api/groups", "");
    const itemFor = (input: GroupInput) => input.catalogType === "movie" ? movie : series;
    const itemsFor = (input: GroupInput) => input.kind === "smart" || input.memberIds.includes(itemFor(input).id) ? [itemFor(input)] : [];
    const send = (value: unknown) => route.fulfill({ json: value });
    if (path === "/options") return send({ resolutions: ["480p", "720p", "1080p", "2160p"], hdrFormats: ["HDR10", "HDR10+", "Dolby Vision", "HLG", "HDR", "any"], tags: ["heist"], genres: ["Comedy", "Action"] });
    if (path === "/candidates") return send({ items: [url.searchParams.get("catalogType") === "movie" ? movie : series], total: 1, limit: 30, offset: 0 });
    if (path === "/preview") {
      const input = request.postDataJSON() as GroupInput;
      return send({ items: itemsFor(input), total: itemsFor(input).length, limit: 12, offset: 0 });
    }
    if (request.method() === "POST" || request.method() === "PUT") {
      const input = request.postDataJSON() as GroupInput;
      const id = path.slice(1) || `g${Object.keys(groups).length + 1}`;
      groups[id] = input; writes.push(input);
      return send({ id, ...input });
    }
    if (request.method() === "DELETE") { delete groups[path.slice(1)]; return route.fulfill({ status: 204 }); }
    if (path === "") return send(Object.entries(groups).map(([id, input]) => ({ id, ...input, itemCount: itemsFor(input).length, posterUrl: null })));
    const id = path.split("/")[1];
    if (!groups[id]) return route.fulfill({ status: 404, json: { detail: "Group not found" } });
    if (path.endsWith("/definition")) return send({ id, ...groups[id] });
    return send({ id, ...groups[id], items: itemsFor(groups[id]), total: itemsFor(groups[id]).length, limit: 60, offset: 0 });
  });
  return { groups, writes };
}

async function choice(page: Page, label: string, value: string) {
  await page.getByRole("combobox", { name: label, exact: true }).click();
  await page.getByRole("option", { name: value, exact: true }).click();
}

test("admin creates a manual group in Settings, browses its folder, edits membership and deletes it", async ({ page }) => {
  const { writes } = await mockGroups(page);
  await page.goto("/settings?tab=groups");
  await page.getByRole("button", { name: "Add group", exact: true }).click();
  await page.getByRole("textbox", { name: "Name", exact: true }).fill("Weekend");
  await page.getByRole("checkbox", { name: "Retro Film (1999)" }).check();
  await page.getByRole("button", { name: "Save group", exact: true }).click();
  await expect(page.getByRole("button", { name: "Edit Weekend" })).toBeVisible();
  expect(writes[0].memberIds).toEqual([movie.id]);
  await page.goto("/groups");
  const navigation = await page.getByRole("navigation").first().getByRole("link").allTextContents();
  expect(navigation.indexOf("Groups")).toBe(navigation.indexOf("Series") + 1);
  expect(navigation.indexOf("Collections")).toBe(navigation.indexOf("Groups") + 1);
  await page.getByRole("link", { name: /Weekend/ }).click();
  await expect(page.getByRole("heading", { name: "Weekend" })).toBeVisible();
  await expect(page.getByRole("link", { name: /Retro Film/ })).toHaveCount(1);
  await page.goto("/settings?tab=groups");
  await page.getByRole("button", { name: "Edit Weekend" }).click();
  await page.getByRole("checkbox", { name: "Retro Film (1999)" }).uncheck();
  await page.getByRole("button", { name: "Save group", exact: true }).click();
  await expect(page.getByText("Movies · Manual · 0 titles")).toBeVisible();
  await page.getByRole("button", { name: "Delete Weekend" }).click();
  await page.getByRole("button", { name: "Delete group", exact: true }).click();
  await expect(page.getByText("No groups yet. Create your first group.")).toBeVisible();
});

test("smart series group offers every HDR format, tags and genres and previews before saving", async ({ page }) => {
  const { writes } = await mockGroups(page);
  await page.goto("/settings?tab=groups");
  await page.getByRole("button", { name: "Add group", exact: true }).click();
  await page.getByRole("textbox", { name: "Name", exact: true }).fill("Retro HDR Series");
  await choice(page, "Catalog type", "Series");
  await choice(page, "Group type", "Smart");
  await page.getByRole("button", { name: "Add condition", exact: true }).click();
  await choice(page, "Condition 2 field", "HDR format");
  await page.getByRole("combobox", { name: "Condition 2 value", exact: true }).click();
  for (const name of ["HDR10", "HDR10+", "Dolby Vision", "HLG", "HDR (unspecified type)", "Any HDR"])
    await expect(page.getByRole("option", { name, exact: true })).toBeVisible();
  await page.getByRole("option", { name: "Any HDR", exact: true }).click();
  await page.getByRole("button", { name: "Add condition", exact: true }).click();
  await choice(page, "Condition 3 field", "Tag");
  await page.getByRole("combobox", { name: "Condition 3 value", exact: true }).fill("heist");
  await page.getByRole("button", { name: "Add condition", exact: true }).click();
  await choice(page, "Condition 4 field", "Genre");
  await page.getByRole("combobox", { name: "Condition 4 value", exact: true }).fill("Comedy");
  await page.getByRole("button", { name: "Preview matches" }).click();
  await expect(page.getByText("1 matching title")).toBeVisible();
  await page.screenshot({ path: "/tmp/media-groups-editor.png", fullPage: true });
  await page.getByRole("button", { name: "Save group", exact: true }).click();
  await expect(page.getByRole("button", { name: "Edit Retro HDR Series" })).toBeVisible();
  expect(writes[0]).toMatchObject({ catalogType: "series", kind: "smart", memberIds: [], rules: { match: "all", conditions: [
    { field: "year", operator: "before", value: "2000" }, { field: "hdr", value: "any" }, { field: "tag", value: "heist" }, { field: "genre", value: "Comedy" },
  ] } });
  await page.goto("/groups");
  await page.screenshot({ path: "/tmp/media-groups-folders.png", fullPage: true });
  await page.getByRole("link", { name: /Retro HDR Series/ }).click();
  await expect(page.getByRole("link", { name: /Retro Series/ })).toHaveAttribute("href", /\/series\/series-one/);
});

test("ordinary viewers can browse but do not see group settings", async ({ page }) => {
  await mockGroups(page, { weekend: manual }, "user");
  await page.goto("/settings?tab=groups");
  await expect(page.getByRole("tab", { name: "Groups", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Add group" })).toHaveCount(0);
  await page.goto("/groups");
  await page.getByRole("link", { name: /Weekend/ }).click();
  await expect(page.getByRole("link", { name: /Retro Film/ })).toBeVisible();
});

test("year rules do not fetch choices or show a cached choices error", async ({ page }) => {
  await mockGroups(page);
  let optionRequests = 0;
  await page.route("**/api/proxy/api/groups/options?**", route => {
    optionRequests++;
    return route.fulfill({ status: 503 });
  });
  await page.goto("/settings?tab=groups");
  await page.getByRole("button", { name: "Add group", exact: true }).click();
  await page.getByRole("textbox", { name: "Name", exact: true }).fill("Retro");
  await choice(page, "Group type", "Smart");
  await page.getByRole("button", { name: "Preview matches" }).click();
  await expect(page.getByText("1 matching title")).toBeVisible();
  expect(optionRequests).toBe(0);
  await expect(page.getByText("Could not load choices.")).toHaveCount(0);

  await choice(page, "Condition 1 field", "HDR format");
  await expect(page.getByText("Could not load choices.")).toBeVisible();
  expect(optionRequests).toBe(1);
  await choice(page, "Condition 1 field", "Release year");
  await page.getByRole("spinbutton", { name: "Condition 1 value", exact: true }).fill("1990");
  await page.getByRole("button", { name: "Preview matches" }).click();
  await expect(page.getByText("1 matching title")).toBeVisible();
  await expect(page.getByText("Could not load choices.")).toHaveCount(0);
  expect(optionRequests).toBe(1);
});

test("failed group list offers retry instead of an empty folder list", async ({ page }) => {
  await mockGroups(page);
  let failed = true;
  await page.route("**/api/proxy/api/groups", route => failed ? route.fulfill({ status: 503 }) : route.fulfill({ json: [] }));
  await page.goto("/groups");
  await expect(page.getByRole("button", { name: /retry|try again/i })).toBeVisible();
  failed = false;
  await page.getByRole("button", { name: /retry|try again/i }).click();
  await expect(page.getByText(/No groups yet/)).toBeVisible();
});
