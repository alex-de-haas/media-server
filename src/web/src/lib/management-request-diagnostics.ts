import { randomUUID } from "node:crypto";

export const managementRequestIdHeader = "X-Management-Request-Id";

function routeName(path: string[]): string | null {
  if (path.join("/") === "api/library/availability") return "availability";
  if (path[0] !== "api" || path[1] !== "management" || path[2] !== "items") return null;
  if (path.length === 3) return "items";
  if (path.length === 5 && (path[4] === "sources" || path[4] === "location")) return path[4];
  return null;
}

/** Measures the proxy's header and body waits without logging URLs, identities or response data. */
export class ManagementRequestDiagnostics {
  readonly requestId = randomUUID();
  private readonly started = performance.now();
  private stageStarted = this.started;
  private stage = "setup";
  private ended = false;
  private bytes = 0;
  private status?: number;
  private readonly timer: ReturnType<typeof setInterval>;
  private readonly ray: string | undefined;
  private readonly aborted = () => this.finish("client_disconnected");

  static start(request: Request, path: string[]): ManagementRequestDiagnostics | null {
    const route = routeName(path);
    return request.method === "GET" && route ? new ManagementRequestDiagnostics(request, route) : null;
  }

  private constructor(private readonly request: Request, private readonly route: string) {
    const ray = request.headers.get("cf-ray");
    this.ray = ray && /^[a-f0-9]{16,32}(?:-[a-z0-9]{3,10})?$/i.test(ray) ? ray : undefined;
    this.report("started");
    this.timer = setInterval(() => this.report("still_running", {}, "warn"), 10_000);
    this.timer.unref?.();
    request.signal.addEventListener("abort", this.aborted, { once: true });
    if (request.signal.aborted) this.aborted();
  }

  upstreamStarted() {
    this.stage = "upstream";
    this.stageStarted = performance.now();
    this.report("upstream_started");
  }

  responseBody(upstream: Response): ReadableStream<Uint8Array> | null {
    this.status = upstream.status;
    this.report("upstream_headers");
    this.stage = "body";
    this.stageStarted = performance.now();
    if (!upstream.body) {
      this.finish("completed");
      return null;
    }
    const reader = upstream.body.getReader();
    let cancelled = false;
    // Read one chunk at a time, preserving backpressure and cancellation of the original body.
    return new ReadableStream<Uint8Array>({
      pull: async controller => {
        try {
          const { done, value } = await reader.read();
          if (cancelled) return;
          if (done) {
            this.finish("completed");
            reader.releaseLock();
            controller.close();
          } else {
            this.bytes += value.byteLength;
            controller.enqueue(value);
          }
        } catch (error) {
          if (cancelled) return;
          this.finish("body_failed", undefined, error);
          reader.releaseLock();
          controller.error(error);
        }
      },
      cancel: async reason => {
        cancelled = true;
        this.finish("body_cancelled");
        try { await reader.cancel(reason); }
        finally { reader.releaseLock(); }
      },
    });
  }

  finish(outcome: string, status?: number, error?: unknown) {
    if (this.ended) return;
    this.status = status ?? this.status;
    const errorType = error instanceof Error && /^[a-z0-9_]{1,64}$/i.test(error.name) ? error.name : undefined;
    const cause = error instanceof Error ? error.cause : undefined;
    const code = cause && typeof cause === "object" && "code" in cause ? cause.code : undefined;
    const errorCode = typeof code === "string" && /^[a-z0-9_]{1,64}$/i.test(code) ? code : undefined;
    this.report(outcome, { errorType, errorCode }, error ? "error" : "info");
    this.ended = true;
    clearInterval(this.timer);
    this.request.signal.removeEventListener("abort", this.aborted);
  }

  private report(event: string, extra: Record<string, unknown> = {}, level: "info" | "warn" | "error" = "info") {
    if (this.ended) return;
    console[level]("[management-request]", JSON.stringify({
      requestId: this.requestId, ray: this.ray, route: this.route, event, stage: this.stage,
      stageElapsedMs: Math.round(performance.now() - this.stageStarted),
      totalElapsedMs: Math.round(performance.now() - this.started), status: this.status, bytes: this.bytes, ...extra,
    }));
  }
}
