// The local ⇄ UTC conversion behind logging a watch by hand. Kept free of React so the dialog and its
// tests share one derivation.
//
// A person states when they watched something in their own wall-clock time; the API stores an instant.
// `<input type="datetime-local">` is the only field involved, and its value is wall-clock text with no
// zone at all — so both directions have to go through the browser's local zone deliberately. Doing it
// with string surgery on an ISO instant instead would shift every logged play by the UTC offset.

/** How far ahead of the browser's clock an instant may be before the dialog refuses it. */
export const FUTURE_ALLOWANCE_MS = 5 * 60 * 1000;

/** Formats an instant as the wall-clock text `<input type="datetime-local">` expects, to the minute. */
export function toLocalInputValue(instant: Date): string {
  const pad = (value: number) => value.toString().padStart(2, "0");
  return (
    `${instant.getFullYear()}-${pad(instant.getMonth() + 1)}-${pad(instant.getDate())}` +
    `T${pad(instant.getHours())}:${pad(instant.getMinutes())}`
  );
}

/**
 * Reads that field back as a UTC instant, or null when it is empty or nonsense. `new Date(value)`
 * interprets a zone-less datetime-local value as local time, which is exactly what was typed.
 */
export function toUtcInstant(value: string): string | null {
  if (!value) {
    return null;
  }

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

/**
 * Whether an instant is far enough ahead to refuse. Mirrors the server's allowance so the dialog can
 * say so before the round trip — the server still enforces it, because a client clock proves nothing.
 */
export function isFutureInstant(instant: string, now: Date = new Date()): boolean {
  return new Date(instant).getTime() > now.getTime() + FUTURE_ALLOWANCE_MS;
}
