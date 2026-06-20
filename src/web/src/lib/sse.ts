// Server-Sent Events client over the same-origin BFF. We use fetch + a ReadableStream reader (rather than
// the native EventSource) so we can attach the identity bearer — EventSource can't set headers, and the
// Shell embeds us cross-site where the cookie alone may be blocked. Reconnects with backoff.

import { getBearerToken } from "@/lib/api";

export interface SseHandlers {
  onEvent: (event: string, data: unknown) => void;
  /** Called with true once the stream is open, false when it drops. */
  onStatus?: (connected: boolean) => void;
}

/** Opens the stream and keeps it open (reconnecting on failure). Returns a disposer to close it. */
export function openEventStream(path: string, handlers: SseHandlers): () => void {
  let closed = false;
  let controller: AbortController | null = null;
  let attempt = 0;
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null;

  async function connect(): Promise<void> {
    if (closed) {
      return;
    }

    controller = new AbortController();
    const token = getBearerToken();
    try {
      const response = await fetch(path, {
        method: "GET",
        headers: {
          accept: "text/event-stream",
          ...(token ? { authorization: `Bearer ${token}` } : {}),
        },
        credentials: "include",
        cache: "no-store",
        signal: controller.signal,
      });
      if (!response.ok || !response.body) {
        throw new Error(`SSE failed: ${response.status}`);
      }

      attempt = 0;
      handlers.onStatus?.(true);
      await pump(response.body, handlers.onEvent);
    } catch {
      // Network error, non-2xx, or the stream ended — fall through to reconnect.
    }

    // Don't fire callbacks or schedule a reconnect once disposed (e.g. React effect cleanup aborted us).
    if (closed) {
      return;
    }

    handlers.onStatus?.(false);
    attempt = Math.min(attempt + 1, 6);
    const delay = Math.min(1000 * 2 ** attempt, 30_000);
    reconnectTimer = setTimeout(() => void connect(), delay);
  }

  void connect();

  return () => {
    closed = true;
    if (reconnectTimer) {
      clearTimeout(reconnectTimer);
    }
    controller?.abort();
  };
}

async function pump(body: ReadableStream<Uint8Array>, onEvent: (event: string, data: unknown) => void): Promise<void> {
  const reader = body.getReader();
  try {
    const decoder = new TextDecoder();
    let buffer = "";

    for (;;) {
      const { value, done } = await reader.read();
      if (done) {
        break;
      }

      buffer += decoder.decode(value, { stream: true });
      let separator: number;
      // Frames are separated by a blank line; tolerate both \n\n and \r\n\r\n.
      while ((separator = indexOfFrameBreak(buffer)) !== -1) {
        const frame = buffer.slice(0, separator);
        buffer = buffer.slice(separator + frameBreakLength(buffer, separator));
        const parsed = parseSseFrame(frame);
        if (parsed) {
          onEvent(parsed.event, parsed.data);
        }
      }
    }
  } finally {
    // Always release the lock so the stream can be GC'd / re-read on reconnect.
    reader.releaseLock();
  }
}

function indexOfFrameBreak(buffer: string): number {
  const lf = buffer.indexOf("\n\n");
  const crlf = buffer.indexOf("\r\n\r\n");
  if (lf === -1) return crlf;
  if (crlf === -1) return lf;
  return Math.min(lf, crlf);
}

function frameBreakLength(buffer: string, at: number): number {
  return buffer.startsWith("\r\n\r\n", at) ? 4 : 2;
}

/** Parses one SSE frame into its event name + JSON-decoded data. Returns null for comments/heartbeats. */
export function parseSseFrame(frame: string): { event: string; data: unknown } | null {
  let event = "message";
  const dataLines: string[] = [];

  for (const rawLine of frame.split("\n")) {
    const line = rawLine.replace(/\r$/, "");
    if (line.length === 0 || line.startsWith(":")) {
      continue; // blank or comment/heartbeat line
    }
    if (line.startsWith("event:")) {
      event = line.slice("event:".length).trim();
    } else if (line.startsWith("data:")) {
      dataLines.push(line.slice("data:".length).replace(/^ /, ""));
    }
  }

  if (dataLines.length === 0) {
    return null;
  }

  try {
    return { event, data: JSON.parse(dataLines.join("\n")) };
  } catch {
    return null;
  }
}
