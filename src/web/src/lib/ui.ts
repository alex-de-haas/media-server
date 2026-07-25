import { ApiError } from "@/lib/api";

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return error.message || `Request failed (${error.status}).`;
  }
  return error instanceof Error ? error.message : "Unexpected error.";
}

/** Opens a URL in a new tab without handing the opener over to it. */
export function openExternal(url: string) {
  const opened = window.open(url, "_blank", "noopener,noreferrer");
  if (opened) {
    opened.opener = null;
  }
}

// Compact vote counts: 12345 → "12K". Pin the locale so SSR and the client format identically (avoids a
// hydration mismatch); the UI is English-only.
const countFormatter = new Intl.NumberFormat("en", { notation: "compact", maximumFractionDigits: 1 });

export function formatCount(value: number) {
  return countFormatter.format(value);
}
