import type { BlurayPlaylist } from "./media-server";

export function describePlaylists(playlists: BlurayPlaylist[], expectedSeconds?: number) {
  const sorted = [...playlists].sort((a, b) =>
    Number(Boolean(a.error)) - Number(Boolean(b.error)) ||
    b.durationSeconds - a.durationSeconds || a.id.localeCompare(b.id));
  const longest = sorted.find(p => !p.error && p.durationSeconds > 0)?.durationSeconds ?? 0;
  // Duration is only a hint: alternate cuts and repeated titles can be equally long.
  const matchesRuntime = expectedSeconds != null && expectedSeconds > 0
    ? sorted.filter(p => !p.error && p.durationSeconds > 0 && Math.abs(p.durationSeconds - expectedSeconds) <= expectedSeconds * 0.1)
    : [];
  const candidates = matchesRuntime.length ? matchesRuntime
    : sorted.filter(p => !p.error && longest > 0 && p.durationSeconds >= longest * 0.9);
  const candidateIds = new Set(candidates.map(p => p.id));
  if (matchesRuntime.length) sorted.sort((a, b) => Number(candidateIds.has(b.id)) - Number(candidateIds.has(a.id)));
  return { sorted, candidateIds, ambiguous: candidates.length > 1, matchedRuntime: matchesRuntime.length > 0 };
}

export function playlistDuration(seconds: number) {
  const total = Math.max(0, Math.floor(seconds));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor(total / 60) % 60;
  const remainder = total % 60;
  return `${hours ? `${hours}h ` : ""}${minutes}m ${remainder}s`;
}
