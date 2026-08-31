import { describe, expect, it } from "vitest";
import { libraryScanSummary, scanSummary } from "@/lib/catalog-scan";
import type { CatalogScanReport, LibraryScanReport } from "@/lib/media-server";

const report = (overrides: Partial<CatalogScanReport> = {}): CatalogScanReport => ({
  catalogId: "c1",
  catalogName: "Movies",
  offline: false,
  filesScanned: 0,
  imported: 0,
  skipped: 0,
  sourcesChecked: 0,
  missingFiles: 0,
  versionsRemoved: 0,
  sidecarsRemoved: 0,
  titlesGhosted: 0,
  titlesPurged: 0,
  missingPaths: [],
  ...overrides,
});

describe("scanSummary", () => {
  it("says the volume is gone rather than reporting an empty scan", () => {
    // The counts are zero because the scan declined to act. Reporting them as findings would tell the
    // operator their library is fine when nothing was actually looked at.
    const { title, description } = scanSummary(report({ offline: true }));

    expect(title).toBe("Movies is offline");
    expect(description).toMatch(/Reconnect the volume/);
  });

  it("leads with removals and distinguishes a kept history from a deletion", () => {
    const { title, description } = scanSummary(
      report({ missingFiles: 3, titlesGhosted: 1, titlesPurged: 2, imported: 1 }),
    );

    expect(title).toBe("3 files gone from disk");
    expect(description).toContain("1 title left the library but kept their watch history");
    expect(description).toContain("2 titles nobody had watched were deleted");
    expect(description).toContain("Importing 1 file");
  });

  it("reports a version dropped from a title that survived it", () => {
    const { description } = scanSummary(report({ missingFiles: 1, versionsRemoved: 1 }));

    expect(description).toContain("1 version removed from titles that kept another");
  });

  it("says what was imported when nothing was lost", () => {
    const { title, description } = scanSummary(report({ imported: 2, skipped: 5, filesScanned: 7 }));

    expect(title).toBe("Importing 2 files — track it on Activity");
    expect(description).toBe("5 already in the library");
  });

  it("says a quiet scan was a scan", () => {
    const { title, description } = scanSummary(report({ sourcesChecked: 40 }));

    expect(title).toBe("Nothing changed");
    expect(description).toBe("40 files checked, all present.");
  });
});

describe("libraryScanSummary", () => {
  const library = (overrides: Partial<LibraryScanReport> = {}): LibraryScanReport => ({
    catalogs: [],
    catalogsScanned: 2,
    catalogsOffline: 0,
    imported: 0,
    sourcesChecked: 0,
    missingFiles: 0,
    versionsRemoved: 0,
    sidecarsRemoved: 0,
    titlesGhosted: 0,
    titlesPurged: 0,
    ...overrides,
  });

  it("names the catalogs it could not read, so a partial pass is not read as a whole one", () => {
    const { title, description } = libraryScanSummary(library({ sourcesChecked: 500, catalogsOffline: 1 }));

    expect(title).toBe("Library is in sync");
    expect(description).toBe("500 files checked across 2 catalogs, 1 catalog offline and left alone.");
  });

  it("totals removals across catalogs", () => {
    const { title, description } = libraryScanSummary(library({ titlesGhosted: 2, titlesPurged: 1 }));

    expect(title).toBe("Library updated");
    expect(description).toContain("2 titles left the library but kept their watch history");
    expect(description).toContain("1 title nobody had watched were deleted");
  });
});
