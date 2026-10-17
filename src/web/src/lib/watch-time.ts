// The local ⇄ UTC conversion behind logging a watch by hand. Kept free of React so the dialog and its
// tests share one derivation.
//
// A person states when they watched something in their own wall-clock time; the API stores an instant.
// The date picker and 24-hour time field compose wall-clock text with no zone at all,
// so both directions have to go through the browser's local zone deliberately. Doing it
// with string surgery on an ISO instant instead would shift every logged play by the UTC offset.

/** How far ahead of the browser's clock an instant may be before the dialog refuses it. */
export const FUTURE_ALLOWANCE_MS = 5 * 60 * 1000;

/** Formats an instant as local YYYY-MM-DDTHH:mm text for the date and time fields. */
export function toLocalInputValue(instant: Date): string {
  const pad = (value: number) => value.toString().padStart(2, "0");
  return (
    `${instant.getFullYear()}-${pad(instant.getMonth() + 1)}-${pad(instant.getDate())}` +
    `T${pad(instant.getHours())}:${pad(instant.getMinutes())}`
  );
}

/**
 * Reads the fields back as a UTC instant. Only a complete date and 00:00–23:59 time are accepted;
 * Date's permissive parser must not turn 24:00 into tomorrow or accept browser-specific formats.
 * A zone-less ISO value is interpreted as local time, which is exactly what was typed.
 */
export function toUtcInstant(value: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}T(?:[01]\d|2[0-3]):[0-5]\d$/.test(value)) {
    return null;
  }

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) || toLocalInputValue(parsed).slice(0, 10) !== value.slice(0, 10)
    ? null : parsed.toISOString();
}

/**
 * Whether an instant is far enough ahead to refuse. Mirrors the server's allowance so the dialog can
 * say so before the round trip — the server still enforces it, because a client clock proves nothing.
 */
export function isFutureInstant(instant: string, now: Date = new Date()): boolean {
  return new Date(instant).getTime() > now.getTime() + FUTURE_ALLOWANCE_MS;
}
