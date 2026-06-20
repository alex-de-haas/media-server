import { describe, expect, it } from "vitest";
import { parseSseFrame } from "./sse";

describe("parseSseFrame", () => {
  it("parses an event name and JSON data", () => {
    const frame = `event: downloadProgress\ndata: {"downloadId":"d1","percentComplete":42}`;
    expect(parseSseFrame(frame)).toEqual({ event: "downloadProgress", data: { downloadId: "d1", percentComplete: 42 } });
  });

  it("defaults the event name to message when none is given", () => {
    expect(parseSseFrame(`data: {"x":1}`)).toEqual({ event: "message", data: { x: 1 } });
  });

  it("tolerates CRLF line endings", () => {
    expect(parseSseFrame(`event: jobStarted\r\ndata: {"jobId":"j1"}`)).toEqual({
      event: "jobStarted",
      data: { jobId: "j1" },
    });
  });

  it("returns null for a heartbeat comment or empty frame", () => {
    expect(parseSseFrame(": ping")).toBeNull();
    expect(parseSseFrame("")).toBeNull();
  });

  it("returns null for malformed JSON", () => {
    expect(parseSseFrame(`event: x\ndata: {not json}`)).toBeNull();
  });
});
