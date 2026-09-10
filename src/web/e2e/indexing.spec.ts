import { test, expect } from "@playwright/test";
import { aMovie, aSeries, anEpisode, movieDetail, episodeDetail, seriesDetail, setupApp } from "./support";

for (const kind of ["movie", "episode"] as const) {
  test(`${kind} indexing uses SSE and reconciles a missed completion after reconnect`, async ({ page }) => {
    const id = kind === "movie" ? "m1" : "e1";
    if (kind === "movie") await page.setViewportSize({ width: 375, height: 812 });
    const source = {
      id: "source-1", fileName: "Arrival.mkv", container: "mkv", versionName: null,
      sizeBytes: 10_000, durationTicks: 600_000_000, streams: [],
      indexing: { state: "waiting", percent: null as number | null, revision: 1 },
    };
    const detail = { ...(kind === "movie" ? movieDetail(id, "Arrival") : episodeDetail(id, "Pilot")), mediaSources: [source] };
    await setupApp(page, {
      role: "user",
      library: kind === "movie" ? [aMovie(id, "Arrival")] : [aSeries("s1", "Series")],
      detail: { [id]: detail, s1: seriesDetail("s1", "Series") },
      episodes: { s1: [anEpisode(id, 1, 1, "Pilot")] },
    });
    await page.addInitScript(() => {
      const original = window.fetch;
      type Harness = Window & { indexingStream?: ReadableStreamDefaultController<Uint8Array>; indexingConnections?: number };
      const harness = window as Harness;
      window.fetch = async (input, init) => {
        if (String(input).endsWith("/api/proxy/api/events")) {
          harness.indexingConnections = (harness.indexingConnections ?? 0) + 1;
          return new Response(new ReadableStream<Uint8Array>({
            start(controller) { harness.indexingStream = controller; },
          }), { headers: { "content-type": "text/event-stream" } });
        }
        return original(input, init);
      };
    });
    let reads = 0;
    page.on("request", request => { if (request.url().endsWith(`/api/library/${id}`)) reads++; });
    await page.goto(kind === "movie" ? `/movies/${id}` : "/series/s1");
    await page.getByRole("tab", { name: kind === "movie" ? "Media" : "Episodes" }).click();
    if (kind === "episode") await page.getByRole("button", { name: "Show media" }).click();
    await expect(page.getByText("Waiting for indexing", { exact: true })).toBeVisible();
    const initialReads = reads;
    const emit = async (state: string, revision: number, percent: number | null = null) => {
      await page.evaluate(({ state, revision, percent, id }) => {
        const stream = (window as unknown as Window & { indexingStream: ReadableStreamDefaultController<Uint8Array> }).indexingStream;
        stream.enqueue(new TextEncoder().encode(`event: indexingChanged\ndata: ${JSON.stringify({ itemId: id, sourceId: "source-1", indexing: { state, revision, percent } })}\n\n`));
      }, { state, revision, percent, id });
    };
    await emit("indexing", 2, 42);
    await expect(page.getByRole("progressbar", { name: "Indexing progress" })).toHaveAttribute("aria-valuenow", "42");
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: `/tmp/indexing-${kind}.png`, fullPage: true });
    await emit("waiting", 1);
    await expect(page.getByText("Indexing · 42%", { exact: true })).toBeVisible();
    await emit("saving", 3);
    await expect(page.getByText("Saving index", { exact: true })).toBeVisible();
    expect(reads).toBe(initialReads);
    // The terminal event is missed while disconnected. The next connection must re-read detail.
    source.indexing = { state: "ready", percent: null, revision: 4 };
    await page.evaluate(() => (window as unknown as Window & { indexingStream: ReadableStreamDefaultController<Uint8Array> }).indexingStream.close());
    await expect.poll(() => reads).toBeGreaterThan(initialReads);
    await expect(page.getByText(/Saving index/)).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as Window & { indexingConnections: number }).indexingConnections)).toBe(2);
  });
}
