import { createElement } from "react";
import { renderToString } from "react-dom/server";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, expect, it, vi } from "vitest";
import { IndexingIndicator } from "./indexing-status";
import { applyIndexingEvent } from "@/lib/indexing";

afterEach(() => vi.restoreAllMocks());

it("reads snapshots and SSE cache entries without development query errors or fetching", () => {
  const errors = vi.spyOn(console, "error").mockImplementation(() => {});
  const client = new QueryClient();
  const render = () => renderToString(createElement(QueryClientProvider, { client },
    createElement(IndexingIndicator, {
      id: "SOURCE", snapshot: { state: "waiting", percent: null, revision: 1 },
    })));
  try {
    expect(render()).toContain("Waiting for indexing");
    applyIndexingEvent(client, {
      sourceId: "source", indexing: { state: "indexing", percent: 42, revision: 2 },
    });
    client.setQueryData(["indexing-connected"], false);
    const html = render();
    expect(html).toContain('aria-valuenow="42"');
    expect(html).toContain("Reconnecting");
    expect(client.isFetching()).toBe(0);
    expect(errors).not.toHaveBeenCalled();
  } finally {
    client.clear();
  }
});
