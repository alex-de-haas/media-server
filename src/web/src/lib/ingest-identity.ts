import type { IngestItem, IngestSourceFile } from "@/lib/media-server";

export function displayIngestPath(relativePath: string): string {
  return relativePath.replace(/^\.incoming\/[0-9a-f]{32}\//i, "");
}

function confirmedMovies(item: IngestItem): IngestSourceFile[] {
  return item.sourceFiles.filter(
    (file) => file.companionKind == null && file.assignmentStatus === "Confirmed" &&
      file.mediaItemId != null && file.assigned?.kind === "Movie",
  );
}

/** Per-file matches exist before Identify sets the batch's primary media item. */
export function ingestTitle(item: IngestItem): string {
  const movies = confirmedMovies(item);
  const movieCount = new Set(movies.map((file) => file.mediaItemId)).size;
  const matchedTitle = movies.find((file) => file.assigned?.title.trim())?.assigned?.title;
  const movieTitle = item.stagesCompleted.includes("identify")
    ? item.mediaTitle || matchedTitle
    : matchedTitle || item.mediaTitle;
  if (movieTitle) return movieCount > 1 ? `${movieTitle} (+${movieCount - 1} more)` : movieTitle;
  if (item.targetTitle) return item.targetTitle;
  const parsed = item.sourceFiles.find((file) => file.parsedTitle?.trim())?.parsedTitle?.trim();
  if (parsed) return parsed;
  if (item.downloadName) return item.downloadName;
  const first = item.sourceFiles[0]?.relativePath;
  return first ? displayIngestPath(first) : "Untitled item";
}

/** A partial match must not imply that every video has been identified. */
export function ingestMatchLabel(item: IngestItem): string | null {
  if (item.status === "Done" || item.stagesCompleted.includes("identify")) return null;
  const matched = confirmedMovies(item).length;
  if (matched === 0) return null;
  const total = item.sourceFiles.filter(
    (file) => file.companionKind == null && file.assignmentStatus !== "Skipped",
  ).length;
  return `Matched ${matched} of ${total} video files`;
}
