import { describe, expect, it } from "vitest";
import { isFutureInstant, toLocalInputValue, toUtcInstant } from "@/lib/watch-time";

describe("toLocalInputValue", () => {
  it("formats an instant as zero-padded local wall-clock text", () => {
    expect(toLocalInputValue(new Date(2026, 7, 9, 9, 5))).toBe("2026-08-09T09:05");
    expect(toLocalInputValue(new Date(2026, 0, 1, 0, 0))).toBe("2026-01-01T00:00");
    expect(toLocalInputValue(new Date(2026, 11, 31, 23, 59))).toBe("2026-12-31T23:59");
  });
});

describe("toUtcInstant", () => {
  it("round-trips the wall-clock time the user typed", () => {
    // Whatever zone the test host is in, the pair has to agree: what goes into the field is what comes
    // back out of it. Anything else silently shifts every logged play by the UTC offset.
    const typed = "2026-08-09T21:30";

    expect(toLocalInputValue(new Date(toUtcInstant(typed)!))).toBe(typed);
  });

  it("round-trips across a daylight-saving boundary", () => {
    // Two dates six months apart sit on opposite sides of DST in most zones, so a conversion that
    // hard-coded a single offset would fail one of them.
    for (const typed of ["2026-01-15T14:00", "2026-07-15T14:00"]) {
      expect(toLocalInputValue(new Date(toUtcInstant(typed)!))).toBe(typed);
    }
  });

  it("produces a UTC instant, not local text", () => {
    expect(toUtcInstant("2026-08-09T21:30")).toMatch(/Z$/);
  });

  it("refuses an empty or unparseable field rather than inventing a time", () => {
    expect(toUtcInstant("")).toBeNull();
    expect(toUtcInstant("not a date")).toBeNull();
  });
});

describe("isFutureInstant", () => {
  const now = new Date("2026-08-09T12:00:00Z");

  it("accepts a clock a few minutes ahead of ours", () => {
    // The field is filled from the browser's clock; refusing a "now" that runs a minute fast would
    // fail the most common action there is.
    expect(isFutureInstant("2026-08-09T12:01:00Z", now)).toBe(false);
  });

  it("refuses an instant nobody has reached yet", () => {
    expect(isFutureInstant("2026-08-09T14:00:00Z", now)).toBe(true);
  });

  it("accepts the past", () => {
    expect(isFutureInstant("2019-01-05T20:00:00Z", now)).toBe(false);
  });
});
