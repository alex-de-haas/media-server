"use client";

import { useMutation } from "@tanstack/react-query";
import { FileWarning, Loader2, Stethoscope } from "lucide-react";
import Link from "next/link";
import { toast } from "@/lib/toast";
import { mediaServer, type LibraryScanReport } from "@/lib/media-server";
import { errorMessage } from "@/lib/ui";
import { useSession } from "@/components/app-shell";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

/**
 * On-demand library health check: library files that vanished from disk, and titles published in more
 * than one catalog. The duplicate audit exists because a work is meant to live in exactly one catalog —
 * imports that would break that are refused now, so anything listed here pre-dates the rule and is
 * repaired by moving one copy onto the other (the move merges them into versions of a single item).
 */
export function LibraryHealthSection() {
  const { role } = useSession();

  const scan = useMutation({
    mutationFn: () => mediaServer.scanLibrary(),
    onSuccess: (report: LibraryScanReport) => {
      const healthy = report.missingFiles === 0 && report.crossCatalogDuplicates.length === 0;
      if (healthy) {
        toast.success("Library looks healthy", {
          description: `${report.sourcesChecked} file(s) checked across ${report.catalogsScanned} catalog(s).`,
        });
      }
    },
    onError: (error) => toast.error("Couldn’t scan the library", { description: errorMessage(error) }),
  });

  if (role !== "admin") {
    return null;
  }

  const report = scan.data;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Library health</CardTitle>
        <CardDescription>
          Checks that every library file is still on disk, and that no title is split across two catalogs.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3 text-sm">
        <div>
          <Button variant="secondary" size="sm" disabled={scan.isPending} onClick={() => scan.mutate()}>
            {scan.isPending ? <Loader2 className="animate-spin" /> : <Stethoscope />}
            {scan.isPending ? "Scanning…" : "Run check"}
          </Button>
        </div>

        {report && (
          <p className="text-muted-foreground text-xs">
            {report.sourcesChecked} file(s) checked across {report.catalogsScanned} catalog(s).
          </p>
        )}

        {report && report.missingFiles > 0 && (
          <div className="flex flex-col gap-1 rounded-md border p-3">
            <p className="flex items-center gap-1.5 font-medium">
              <FileWarning className="size-4" aria-hidden />
              {report.missingFiles} missing file(s)
            </p>
            <ul className="text-muted-foreground flex flex-col gap-0.5 font-mono text-xs">
              {report.missingPaths.map((path) => (
                <li key={path} className="truncate" title={path}>
                  {path}
                </li>
              ))}
            </ul>
          </div>
        )}

        {report?.crossCatalogDuplicates.map((duplicate) => (
          <div key={`${duplicate.kind}-${duplicate.title}-${duplicate.year}`} className="flex flex-col gap-1 rounded-md border p-3">
            <p className="font-medium">
              {duplicate.title}
              {duplicate.year != null && <span className="text-muted-foreground font-normal"> ({duplicate.year})</span>}
            </p>
            <p className="text-muted-foreground text-xs">
              Published in {duplicate.copies.map((copy) => copy.catalogName).join(" and ")}. Watched state and
              favorites are tracked separately for each copy — move one into the other catalog to merge them
              into a single title with two versions.
            </p>
            <div className="flex flex-wrap gap-2 pt-1">
              {duplicate.copies.map((copy) => (
                <Button key={copy.mediaItemId} variant="outline" size="sm" render={
                  <Link href={`/${duplicate.kind === "Series" ? "series" : "movies"}/${copy.mediaItemId}`}>
                    Open in {copy.catalogName}
                  </Link>
                } />
              ))}
            </div>
          </div>
        ))}
      </CardContent>
    </Card>
  );
}
