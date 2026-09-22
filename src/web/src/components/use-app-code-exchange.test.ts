import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const hooks = vi.hoisted(() => ({
  effect: null as (() => (() => void) | void) | null,
  setReady: vi.fn(),
}));

// Exercise setup → cleanup → setup with the same mounted hook refs, as Strict Mode does.
// The real Core-managed browser check covers React rendering and Shell navigation.
vi.mock("react", () => ({
  useEffect: (effect: () => (() => void) | void) => { hooks.effect = effect; },
  useRef: (current: unknown) => ({ current }),
  useState: () => [false, hooks.setReady],
}));
vi.mock("@/lib/api", () => ({ setBearerToken: vi.fn() }));
vi.mock("@/components/session-recovery", () => ({ clearRecoveryGuard: vi.fn() }));

import { setBearerToken } from "@/lib/api";
import { clearRecoveryGuard } from "@/components/session-recovery";
import { useAppCodeExchange } from "./use-app-code-exchange";

const fetchMock = vi.fn<typeof fetch>();
let href: string;

beforeEach(() => {
  vi.clearAllMocks();
  href = "http://app.local/?code=single-use&hosty_launch=embedded#recent";
  vi.stubGlobal("fetch", fetchMock);
  vi.stubGlobal("window", {
    location: { get href() { return href; } },
    history: { replaceState: vi.fn((_state, _unused, url: string) => { href = url; }) },
  });
});
afterEach(() => vi.unstubAllGlobals());

async function flush() {
  // Drain the exchange, JSON parsing, and readiness subscriptions.
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

function replayEffect() {
  // React hooks are mocked above to replay the captured effect without a DOM renderer.
  // eslint-disable-next-line react-hooks/rules-of-hooks
  useAppCodeExchange();
  const setup = hooks.effect!;
  const cleanup = setup();
  cleanup?.();
  return setup();
}

describe("app-code exchange readiness", () => {
  it("reveals the first page after a delayed exchange despite effect replay", async () => {
    let resolve!: (response: Response) => void;
    fetchMock.mockReturnValueOnce(new Promise((done) => { resolve = done; }));
    const cleanup = replayEffect();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(hooks.setReady).not.toHaveBeenCalled();

    resolve(Response.json({ accessToken: "test-token" }));
    await flush();

    expect(setBearerToken).toHaveBeenCalledExactlyOnceWith("test-token");
    expect(clearRecoveryGuard).toHaveBeenCalledTimes(1);
    expect(hooks.setReady).toHaveBeenCalledExactlyOnceWith(true);
    expect(href).toBe("http://app.local/?hosty_launch=embedded#recent");
    expect(fetchMock).toHaveBeenCalledExactlyOnceWith("/api/auth/app-code", expect.objectContaining({
      method: "POST", body: JSON.stringify({ code: "single-use" }), credentials: "include",
    }));
    cleanup?.();
  });

  it.each(["rejected", "network"])("allows session recovery after a %s exchange", async (failure) => {
    if (failure === "network") fetchMock.mockRejectedValueOnce(new Error("offline"));
    else fetchMock.mockResolvedValueOnce(new Response(null, { status: 401 }));
    const cleanup = replayEffect();
    await flush();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(hooks.setReady).toHaveBeenCalledExactlyOnceWith(true);
    expect(setBearerToken).not.toHaveBeenCalled();
    expect(clearRecoveryGuard).not.toHaveBeenCalled();
    expect(new URL(href).searchParams.has("code")).toBe(false);
    cleanup?.();
  });

  it("opens an existing session without exchanging a code", async () => {
    href = "http://app.local/movies";
    const cleanup = replayEffect();
    await flush();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(hooks.setReady).toHaveBeenCalledExactlyOnceWith(true);
    cleanup?.();
  });

  it("does not reveal a page after the live effect unmounts", async () => {
    let resolve!: (response: Response) => void;
    fetchMock.mockReturnValueOnce(new Promise((done) => { resolve = done; }));
    const cleanup = replayEffect();
    cleanup?.();
    resolve(Response.json({ accessToken: "test-token" }));
    await flush();
    expect(hooks.setReady).not.toHaveBeenCalled();
  });
});
