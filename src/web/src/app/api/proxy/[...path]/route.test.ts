import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { NextRequest } from "next/server";
import { GET, POST } from "./route";
import { managementRequestIdHeader } from "@/lib/management-request-diagnostics";
import { readIdentityToken } from "@/lib/host-auth";

vi.mock("@/lib/hosty", () => ({ hostyServerEnv: () => ({ apiUrl: "http://internal-api" }) }));
vi.mock("@/lib/host-auth", () => ({ readIdentityToken: vi.fn(() => "private-token") }));

const path = ["api", "management", "items"];
const context = { params: Promise.resolve({ path }) };
const ray = "a4a00c620956f5e3-AMS";
function request(signal?: AbortSignal) {
  return new NextRequest("https://media.example/api/proxy/api/management/items?search=private-title&token=private-secret", {
    headers: { "cf-ray": ray, [managementRequestIdHeader]: "untrusted-private-id" }, signal,
  });
}

describe("management proxy diagnostics", () => {
  let records: Record<string, unknown>[];
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => {
    records = [];
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval", "performance"] });
    for (const level of ["info", "warn", "error"] as const) {
      vi.spyOn(console, level).mockImplementation((prefix: string, data: string) => {
        expect(prefix).toBe("[management-request]");
        records.push(JSON.parse(data));
      });
    }
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
    vi.mocked(readIdentityToken).mockReturnValue("private-token");
  });

  afterEach(() => {
    expect(vi.getTimerCount()).toBe(0);
    expect(JSON.stringify(records)).not.toContain("private-");
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it("correlates proxy/API records and preserves response status, headers and bytes", async () => {
    fetchMock.mockResolvedValue(new Response("private-response-body", { status: 201,
      headers: { "content-type": "application/json", "x-result": "kept", connection: "close" } }));
    const response = await GET(request(), context);
    expect(response.status).toBe(201);
    expect(response.headers.get("x-result")).toBe("kept");
    expect(response.headers.has("connection")).toBe(false);
    expect(await response.text()).toBe("private-response-body");
    const id = response.headers.get(managementRequestIdHeader);
    expect(id).toMatch(/^[a-f0-9-]{36}$/);
    const headers = fetchMock.mock.calls[0][1]!.headers as Headers;
    expect(headers.get(managementRequestIdHeader)).toBe(id);
    expect(headers.get("authorization")).toBe("Bearer private-token");
    expect(records.every(row => row.requestId === id && row.ray === ray)).toBe(true);
    expect(records.map(row => row.event)).toEqual(["started", "upstream_started", "upstream_headers", "completed"]);
    expect(records.at(-1)).toMatchObject({ stage: "body", bytes: 21, status: 201 });
  });

  it("reports a stalled header wait before the fetch completes, then stops its timer", async () => {
    let release!: (response: Response) => void;
    fetchMock.mockImplementation(() => new Promise(resolve => { release = resolve; }));
    const pending = GET(request(), context);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(records.at(-1)).toMatchObject({ event: "still_running", stage: "upstream", stageElapsedMs: 10_000 });
    release(new Response("ok"));
    expect(await (await pending).text()).toBe("ok");
    const count = records.length;
    await vi.advanceTimersByTimeAsync(20_000);
    expect(records).toHaveLength(count);
  });

  it("reports body stalls and forwards cancellation to the upstream stream", async () => {
    const cancel = vi.fn();
    let controller!: ReadableStreamDefaultController<Uint8Array>;
    fetchMock.mockResolvedValue(new Response(new ReadableStream<Uint8Array>({
      start(value) { controller = value; }, cancel,
    })));
    const response = await GET(request(), context);
    const reader = response.body!.getReader();
    const pending = reader.read();
    controller.enqueue(new TextEncoder().encode("first"));
    expect((await pending).value).toEqual(new TextEncoder().encode("first"));
    await vi.advanceTimersByTimeAsync(10_000);
    expect(records.at(-1)).toMatchObject({ event: "still_running", stage: "body", bytes: 5 });
    await reader.cancel("private-cancellation-reason");
    expect(cancel).toHaveBeenCalledWith("private-cancellation-reason");
    expect(records.at(-1)).toMatchObject({ event: "body_cancelled", stage: "body" });
  });

  it("preserves body errors without recording their messages", async () => {
    fetchMock.mockResolvedValue(new Response(new ReadableStream({
      pull(controller) { controller.error(new Error("private-response-error")); },
    })));
    const response = await GET(request(), context);
    await expect(response.text()).rejects.toThrow("private-response-error");
    expect(records.at(-1)).toMatchObject({ event: "body_failed", errorType: "Error" });
  });

  it("keeps the existing 502 contract and records only the fetch error type and code", async () => {
    fetchMock.mockRejectedValue(new TypeError("private-upstream-url", { cause: { code: "UND_ERR_HEADERS_TIMEOUT" } }));
    const response = await GET(request(), context);
    expect(response.status).toBe(502);
    expect(await response.json()).toEqual({ error: "upstream_unreachable" });
    expect(records.at(-1)).toMatchObject({ event: "upstream_failed", errorType: "TypeError", errorCode: "UND_ERR_HEADERS_TIMEOUT", status: 502 });
  });

  it("records caller disconnect during an upstream wait and cleans up immediately", async () => {
    const abort = new AbortController();
    let release!: (response: Response) => void;
    fetchMock.mockImplementation(() => new Promise(resolve => { release = resolve; }));
    const pending = GET(request(abort.signal), context);
    await vi.advanceTimersByTimeAsync(1);
    abort.abort();
    expect(records.at(-1)).toMatchObject({ event: "client_disconnected", stage: "upstream" });
    expect(vi.getTimerCount()).toBe(0);
    release(new Response("ok"));
    await (await pending).text();
  });

  it("records missing identity without calling upstream", async () => {
    vi.mocked(readIdentityToken).mockReturnValue(null);
    const response = await GET(request(), context);
    expect(response.status).toBe(401);
    expect(fetchMock).not.toHaveBeenCalled();
    expect(records.at(-1)).toMatchObject({ event: "unauthenticated", status: 401 });
  });

  it("does not instrument other routes or mutations", async () => {
    fetchMock.mockImplementation(async () => new Response("ok"));
    const other = await GET(request(), { params: Promise.resolve({ path: ["api", "catalogs"] }) });
    await other.text();
    const mutation = await POST(new NextRequest("https://media.example/api/proxy/api/management/items", { method: "POST" }), context);
    await mutation.text();
    expect(records).toEqual([]);
    expect(other.headers.has(managementRequestIdHeader)).toBe(false);
    expect(mutation.headers.has(managementRequestIdHeader)).toBe(false);
  });
});
