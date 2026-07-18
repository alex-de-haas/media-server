// Client-side API helpers. Requests go to the same-origin BFF (`/api/...`), which proxies to the
// backend. The identity cookie rides along via `credentials: "include"`; when the cross-site
// cookie is blocked we additionally send the in-memory/sessionStorage bearer fallback.

const STORAGE_KEY = "hosty_identity";
let bearerToken: string | null = null;

export function setBearerToken(token: string | null): void {
  bearerToken = token;
  if (typeof window === "undefined") {
    return;
  }
  if (token) {
    window.sessionStorage.setItem(STORAGE_KEY, token);
  } else {
    window.sessionStorage.removeItem(STORAGE_KEY);
  }
}

export function getBearerToken(): string | null {
  if (bearerToken) {
    return bearerToken;
  }
  if (typeof window !== "undefined") {
    bearerToken = window.sessionStorage.getItem(STORAGE_KEY);
  }
  return bearerToken;
}

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
  const headers = new Headers(init?.headers);
  const token = getBearerToken();
  if (token && !headers.has("authorization")) {
    headers.set("authorization", `Bearer ${token}`);
  }

  const response = await fetch(path, { ...init, headers, credentials: "include" });
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
