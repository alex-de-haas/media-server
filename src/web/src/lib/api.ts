// SDK transport keeps the app grant in memory and supplies the cookie-free iframe fallback.
import { appFetch } from "@hosty-sdk/app/browser-auth";

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    /** Machine-readable `error` field from a JSON error body, when present. */
    public readonly code: string | null = null,
    /** The parsed JSON error body, when the response carried one. */
    public readonly body: unknown = null,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

export async function apiFetch(path: string, init?: RequestInit): Promise<Response> {
  const response = await appFetch(path, init);
  if (!response.ok) {
    const body = await response.text().catch(() => "");
    const parsed = parseJsonBody(body);
    const code = parsed && typeof (parsed as { error?: unknown }).error === "string"
      ? ((parsed as { error: string }).error || null)
      : null;
    throw new ApiError(response.status, problemMessage(body) || response.statusText, code, parsed);
  }
  return response;
}

function parseJsonBody(body: string): unknown {
  const trimmed = body.trim();
  if (!trimmed.startsWith("{")) {
    return null;
  }
  try {
    return JSON.parse(trimmed) as unknown;
  } catch {
    return null;
  }
}

// ASP.NET errors come back as RFC 9457 problem+json (`{ title, detail, status }`); surface the
// human-readable `detail`/`title` instead of the raw JSON blob. Falls back to the raw text.
function problemMessage(body: string): string {
  const trimmed = body.trim();
  if (!trimmed.startsWith("{")) {
    return trimmed;
  }
  try {
    const parsed = JSON.parse(trimmed) as { detail?: unknown; title?: unknown };
    const message = typeof parsed.detail === "string" ? parsed.detail : parsed.title;
    return typeof message === "string" && message.length > 0 ? message : trimmed;
  } catch {
    return trimmed;
  }
}

export async function apiJson<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await apiFetch(path, init);
  return (await response.json()) as T;
}
