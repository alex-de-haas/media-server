"use client";

import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { Search } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type Catalog, type IngestItem, type MetadataCandidate } from "@/lib/media-server";
import { inputClass, errorMessage } from "@/lib/ui";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

/**
 * The metadata-resolution popup for a NeedsReview ingest item: re-search by a corrected title and
 * pick the right match for each unresolved file. Closes itself once a match is applied.
 */
export function IngestReviewDialog({
  item,
  catalog,
  open,
  onOpenChange,
  onMatched,
}: {
  item: IngestItem;
  catalog: Catalog | undefined;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onMatched: () => void;
}) {
  const isEpisodic = catalog?.type === "Series" || catalog?.type === "Anime";
  const unresolved = item.sourceFiles.filter((file) => file.assignmentStatus === "NeedsReview" || file.mediaItemId == null);

  const [season, setSeason] = useState(1);
  const [episode, setEpisode] = useState(1);
  const [searchTitle, setSearchTitle] = useState("");
  const [searchYear, setSearchYear] = useState("");
  // Results from a manual re-search override the (possibly empty/wrong) auto-identified candidates.
  const [searchResults, setSearchResults] = useState<MetadataCandidate[] | null>(null);

  const search = useMutation({
    mutationFn: () =>
      mediaServer.searchIngest(item.id, {
        title: searchTitle.trim(),
        year: searchYear.trim() ? Number(searchYear) : null,
        kind: isEpisodic ? "Series" : "Movie",
      }),
    onSuccess: (results) => setSearchResults(results),
    onError: (error) => toast.error("Search failed", { description: errorMessage(error) }),
  });

  const match = useMutation({
    mutationFn: ({
      sourceFileId,
      provider,
      providerId,
      title,
      year,
    }: {
      sourceFileId: string;
      provider: string;
      providerId: string;
      title: string;
      year: number | null;
    }) =>
      mediaServer.matchIngest(item.id, {
        sourceFileId,
        kind: isEpisodic ? "Episode" : "Movie",
        provider,
        providerId,
        title,
        year,
        season: isEpisodic ? season : null,
        episode: isEpisodic ? episode : null,
      }),
    onSuccess: () => {
      toast.success("Match applied");
      onMatched();
    },
    onError: (error) => toast.error("Couldn’t apply match", { description: errorMessage(error) }),
  });

  const candidates = searchResults ?? item.reviewCandidates;
  const title = item.downloadName ?? item.sourceFiles[0]?.relativePath ?? "Untitled item";

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-xl">
        <DialogHeader>
          <DialogTitle>Resolve match</DialogTitle>
          <DialogDescription className="truncate" title={title}>
            {title}
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-3 text-sm">
          {/* Re-search by a corrected title when the auto-parsed name was wrong or returned nothing. */}
          <form
            className="flex flex-wrap items-end gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              if (searchTitle.trim()) search.mutate();
            }}
          >
            <label className="flex flex-1 flex-col gap-1">
              <span className="text-muted-foreground text-xs">Corrected title</span>
              <input
                className={`${inputClass} h-8`}
                value={searchTitle}
                placeholder={isEpisodic ? "Series title" : "Movie title"}
                onChange={(e) => setSearchTitle(e.target.value)}
              />
            </label>
            <label className="flex flex-col gap-1">
              <span className="text-muted-foreground text-xs">Year</span>
              <input className={`${inputClass} h-8 w-20`} type="number" value={searchYear} onChange={(e) => setSearchYear(e.target.value)} />
            </label>
            <Button type="submit" variant="secondary" size="sm" disabled={!searchTitle.trim() || search.isPending}>
              <Search />
              {search.isPending ? "Searching…" : "Search"}
            </Button>
          </form>

          {isEpisodic && (
            <div className="text-muted-foreground flex items-center gap-3">
              <label className="flex items-center gap-1.5">
                Season
                <input className={`${inputClass} h-7 w-16`} type="number" min={0} value={season} onChange={(e) => setSeason(Number(e.target.value))} />
              </label>
              <label className="flex items-center gap-1.5">
                Episode
                <input className={`${inputClass} h-7 w-16`} type="number" min={0} value={episode} onChange={(e) => setEpisode(Number(e.target.value))} />
              </label>
            </div>
          )}

          <div className="flex max-h-72 flex-col gap-3 overflow-y-auto">
            {unresolved.map((file) => (
              <div key={file.id} className="flex flex-col gap-1.5">
                <span className="text-muted-foreground truncate font-mono text-xs" title={file.relativePath}>
                  {file.relativePath}
                </span>
                <div className="flex flex-wrap gap-2">
                  {candidates.length ? (
                    candidates.map((candidate) => (
                      <Button
                        key={`${candidate.reference.provider}:${candidate.reference.id}`}
                        variant="outline"
                        size="sm"
                        disabled={match.isPending}
                        onClick={() =>
                          match.mutate({
                            sourceFileId: file.id,
                            provider: candidate.reference.provider,
                            providerId: candidate.reference.id,
                            title: candidate.title,
                            year: candidate.year,
                          })
                        }
                      >
                        {candidate.title}
                        {candidate.year ? ` (${candidate.year})` : ""} · {(candidate.score * 100).toFixed(0)}%
                      </Button>
                    ))
                  ) : (
                    <span className="text-muted-foreground text-xs">
                      {searchResults ? "No matches for that title." : "No candidates returned — try a corrected title above."}
                    </span>
                  )}
                </div>
              </div>
            ))}
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}
