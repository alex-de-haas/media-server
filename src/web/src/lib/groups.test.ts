import { beforeEach, describe, expect, it, vi } from "vitest";
import { apiJson } from "@/lib/api";
import { groupsApi } from "@/lib/groups";

vi.mock("@/lib/api", () => ({ apiJson: vi.fn().mockResolvedValue({ items: [] }), apiFetch: vi.fn() }));

describe("group candidate search", () => {
  beforeEach(() => vi.clearAllMocks());

  it.each(["", "   "])("omits a blank title (%j) while keeping pagination and type", async title => {
    await groupsApi.candidates("series", title, 30);
    const url = new URL(vi.mocked(apiJson).mock.calls[0][0] as string, "http://localhost");
    expect(url.searchParams.has("title")).toBe(false);
    expect(Object.fromEntries(url.searchParams)).toEqual({ catalogType: "series", offset: "30", limit: "30" });
  });

  it("trims and encodes a nonempty search term", async () => {
    await groupsApi.candidates("movie", "  Retro & 100%_\\  ", 0);
    const url = new URL(vi.mocked(apiJson).mock.calls[0][0] as string, "http://localhost");
    expect(url.searchParams.get("title")).toBe("Retro & 100%_\\");
  });
});
