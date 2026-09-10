import { describe, expect, it } from "vitest";
import { QueryClient } from "@tanstack/react-query";
import { applyIndexingEvent, indexingLabel, latestIndexing } from "./indexing";

describe("indexing SSE", () => {
  it("orders initial snapshots, progress, completion and retries by revision", () => {
    const client = new QueryClient();
    const event = (state: string, revision: number, percent: number | null = null) =>
      applyIndexingEvent(client, { sourceId: "SOURCE", indexing: { state, revision, percent } });
    event("indexing", 2, 42);
    event("waiting", 1);
    expect(client.getQueryData(["indexing", "source"])).toEqual({ state: "indexing", revision: 2, percent: 42 });
    event("ready", 3);
    expect(latestIndexing({ state: "indexing", percent: 1, revision: 1 }, client.getQueryData(["indexing", "source"]))?.state).toBe("ready");
    event("indexing", 4, 0);
    expect(client.getQueryData(["indexing", "source"])).toEqual({ state: "indexing", percent: 0, revision: 4 });
    client.clear();
  });
  it("keeps sidecar progress independent and rejects malformed events", () => {
    const client = new QueryClient();
    applyIndexingEvent(client, { sourceId: "video", streamId: "dub", indexing: { state: "saving", percent: null, revision: 1 } });
    applyIndexingEvent(client, { sourceId: "video", indexing: { state: "ready", revision: "2" } });
    expect(client.getQueryData(["indexing", "video"])).toBeUndefined();
    expect(client.getQueryData(["indexing", "dub"])).toMatchObject({ state: "saving" });
    client.clear();
  });
  it("handles waiting, saving, failure, older servers and unknown states", () => {
    expect(indexingLabel()).toBeNull();
    expect(indexingLabel({ state: "future", percent: null, revision: 1 })).toBeNull();
    expect(indexingLabel({ state: "ready", percent: null, revision: 1 })).toBeNull();
    expect(indexingLabel({ state: "waiting", percent: null, revision: 1 })).toBe("Waiting for indexing");
    expect(indexingLabel({ state: "saving", percent: null, revision: 1 })).toBe("Saving index");
    expect(indexingLabel({ state: "failed", percent: null, revision: 1 })).toBe("Indexing could not finish");
    expect(indexingLabel({ state: "indexing", percent: 100, revision: 1 })).toBe("Indexing · 99%");
  });
});
