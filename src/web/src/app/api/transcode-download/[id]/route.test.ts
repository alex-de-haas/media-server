import { afterEach, expect, it, vi } from "vitest";
import { NextRequest } from "next/server";
import { GET } from "./route";
vi.mock("@/lib/hosty", () => ({ hostyServerEnv: () => ({ apiUrl: "http://api.internal" }) }));
afterEach(() => vi.unstubAllGlobals());
const id = "10000000-0000-0000-0000-000000000001";

it("streams a ranged export without forwarding Core grants or buffering its body", async () => {
  const stream = new ReadableStream<Uint8Array>({ start(controller) { controller.enqueue(new Uint8Array([1, 2, 3])); controller.close(); } });
  const upstream = new Response(stream, { status: 206, headers: { "content-range": "bytes 0-2/3", "content-disposition": 'attachment; filename="export.mkv"' } });
  const fetch = vi.fn().mockResolvedValue(upstream); vi.stubGlobal("fetch", fetch);
  const result = await GET(new NextRequest(`https://app.test/api/transcode-download/${id}?token=export-ticket`, {
    headers: { authorization: "Bearer private-grant", cookie: "private-cookie", range: "bytes=0-2" },
  }), { params: Promise.resolve({ id }) });
  expect(result.status).toBe(206);
  expect(result.body).toBe(upstream.body);
  expect(result.headers.get("cache-control")).toBe("private, no-store");
  const [url, init] = fetch.mock.calls[0];
  expect(url).toContain("token=export-ticket");
  expect(JSON.stringify([url, [...init.headers]])).not.toContain("private-");
  expect(init.headers.get("range")).toBe("bytes=0-2");
  expect(await result.arrayBuffer()).toEqual(new Uint8Array([1, 2, 3]).buffer);
});

it("refuses missing tickets and malformed job IDs without contacting the backend", async () => {
  const fetch = vi.fn(); vi.stubGlobal("fetch", fetch);
  for (const [job, query] of [[id, ""], ["../other", "?token=export-ticket"]]) {
    expect((await GET(new NextRequest(`https://app.test/api/transcode-download/${job}${query}`), { params: Promise.resolve({ id: job }) })).status).toBe(404);
  }
  expect(fetch).not.toHaveBeenCalled();
});
