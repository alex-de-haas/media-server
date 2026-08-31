import type { CatalogScanReport, LibraryScanReport } from "@/lib/media-server";

/** `1 file` / `2 files`, for the counts a scan reports. */
export function count(value: number, noun: string, plural = `${noun}s`): string {
  return `${value} ${value === 1 ? noun : plural}`;
}

/**
 * What a scan did, as a toast.
 *
 * A scan has two halves and either can be the whole story, so the message names the ones that actually
 * happened rather than always printing every counter. Removals lead when there were any: a title leaving
 * the library is the part an operator needs to have been told about, and "removed but its history is
 * kept" is the distinction that stops it reading as data loss.
 */
export function scanSummary(report: CatalogScanReport): { title: string; description?: string } {
  if (report.offline) {
    return {
      title: `${report.catalogName} is offline`,
      description:
        "None of its files could be read, so nothing was imported or removed. Reconnect the volume and scan again.",
    };
  }

  const removals = removalPhrases(report);
  const imported =
    report.imported > 0 ? `Importing ${count(report.imported, "file")} — track it on Activity` : null;

  if (removals.length === 0) {
    return {
      title: imported ?? "Nothing changed",
      description:
        imported === null
          ? `${count(report.sourcesChecked, "file")} checked, all present.`
          : report.skipped > 0
            ? `${report.skipped} already in the library`
            : undefined,
    };
  }

  return {
    title: `${count(report.missingFiles, "file")} gone from disk`,
    description: [...removals, imported].filter(Boolean).join(". ") + ".",
  };
}

/** The same, for a pass over every catalog. */
export function libraryScanSummary(report: LibraryScanReport): { title: string; description?: string } {
  const offline = report.catalogsOffline;
  const offlineNote = offline > 0 ? `${count(offline, "catalog")} offline and left alone` : null;

  const removals = removalPhrases(report);
  const imported = report.imported > 0 ? `importing ${count(report.imported, "file")}` : null;

  if (removals.length === 0 && imported === null) {
    return {
      title: "Library is in sync",
      description:
        [`${count(report.sourcesChecked, "file")} checked across ${count(report.catalogsScanned, "catalog")}`, offlineNote]
          .filter(Boolean)
          .join(", ") + ".",
    };
  }

  return {
    title: imported && removals.length === 0 ? `Importing ${count(report.imported, "file")}` : "Library updated",
    description: [...removals, imported, offlineNote].filter(Boolean).join(". ") + ".",
  };
}

function removalPhrases(report: {
  titlesGhosted: number;
  titlesPurged: number;
  versionsRemoved: number;
  sidecarsRemoved: number;
}): string[] {
  const phrases: string[] = [];
  if (report.titlesGhosted > 0) {
    phrases.push(`${count(report.titlesGhosted, "title")} left the library but kept their watch history`);
  }
  if (report.titlesPurged > 0) {
    phrases.push(`${count(report.titlesPurged, "title")} nobody had watched were deleted`);
  }
  if (report.versionsRemoved > 0) {
    phrases.push(`${count(report.versionsRemoved, "version")} removed from titles that kept another`);
  }
  if (report.sidecarsRemoved > 0) {
    phrases.push(`${count(report.sidecarsRemoved, "sidecar track")} removed`);
  }
  return phrases;
}
