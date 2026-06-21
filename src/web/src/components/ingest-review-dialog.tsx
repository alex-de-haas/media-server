"use client";

import { useEffect, useRef, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { Search } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type Catalog, type IngestItem, type IngestSourceFile, type MetadataCandidate } from "@/lib/media-server";
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
 * The metadata-resolution popup for a NeedsReview ingest item. On open it pre-fills the corrected title
 * and (for series/anime) each file's season/episode from the backend's name-parsed hints, and auto-runs a
 * search when no auto-identified candidates exist — so the operator usually just picks a match. The title
 * and per-file season/episode stay editable for the cases the parser got wrong.
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

  const [searchTitle, setSearchTitle] = useState("");
  const [searchYear, setSearchYear] = useState("");
  // Per-file season/episode, keyed by source-file id and seeded from each file's parsed SxxEyy. A whole
  // season pack therefore pre-fills the right number on every file instead of a shared "1/1".
  const [episodeNumbers, setEpisodeNumbers] = useState<Record<string, { season: number; episode: number }>>({});
  // Results from a manual re-search override the (possibly empty/wrong) auto-identified candidates.
  const [searchResults, setSearchResults] = useState<MetadataCandidate[] | null>(null);

  const search = useMutation({
    mutationFn: (variables: { title: string; year: number | null }) =>
      mediaServer.searchIngest(item.id, {
        title: variables.title,
        year: variables.year,
        kind: isEpisodic ? "Series" : "Movie",
      }),
    onSuccess: (results) => setSearchResults(results),
    onError: (error) => toast.error("Search failed", { description: errorMessage(error) }),
  });

  const match = useMutation({
    mutationFn: (variables: {
      sourceFileId: string;
      provider: string;
      providerId: string;
      title: string;
      year: number | null;
      season: number;
      episode: number;
    }) =>
      mediaServer.matchIngest(item.id, {
        sourceFileId: variables.sourceFileId,
        kind: isEpisodic ? "Episode" : "Movie",
        provider: variables.provider,
        providerId: variables.providerId,
        title: variables.title,
        year: variables.year,
        season: isEpisodic ? variables.season : null,
        episode: isEpisodic ? variables.episode : null,
      }),
    onSuccess: () => {
      toast.success("Match applied");
      onMatched();
    },
    onError: (error) => toast.error("Couldn’t apply match", { description: errorMessage(error) }),
  });

  // Re-seed the editable fields whenever the dialog opens for an item: corrected title/year from the first
  // unresolved file (the series title for packs), and per-file season/episode from each file's parse.
  const searchMutate = search.mutate;
  const seededFor = useRef<string | null>(null);
  useEffect(() => {
    if (!open) {
      seededFor.current = null;
      return;
    }
    if (seededFor.current === item.id) return;
    seededFor.current = item.id;

    const first = unresolved[0];
    const parsedTitle = first?.parsedTitle?.trim() ?? "";
    const parsedYear = first?.parsedYear ?? null;
    setSearchTitle(parsedTitle);
    setSearchYear(parsedYear != null ? String(parsedYear) : "");
    setSearchResults(null);
    setEpisodeNumbers(
      Object.fromEntries(unresolved.map((file) => [file.id, { season: file.parsedSeason ?? 1, episode: file.parsedEpisode ?? 1 }])),
    );

    // No auto-identified candidates to show? Run the search up-front so variants appear without a click.
    if (item.reviewCandidates.length === 0 && parsedTitle) {
      searchMutate({ title: parsedTitle, year: parsedYear });
    }
    // unresolved is derived from item each render; keying the effect on item.id keeps it to one run per open.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, item.id]);

  const numbersFor = (file: IngestSourceFile) =>
    episodeNumbers[file.id] ?? { season: file.parsedSeason ?? 1, episode: file.parsedEpisode ?? 1 };

  const setNumbers = (file: IngestSourceFile, patch: Partial<{ season: number; episode: number }>) =>
    setEpisodeNumbers((prev) => ({ ...prev, [file.id]: { ...numbersFor(file), ...patch } }));

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
          {/* Pre-filled from the parsed name; edit + re-search when the auto-parse was wrong. */}
          <form
            className="flex flex-wrap items-end gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              if (searchTitle.trim()) search.mutate({ title: searchTitle.trim(), year: searchYear.trim() ? Number(searchYear) : null });
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

          <div className="flex max-h-72 flex-col gap-3 overflow-y-auto">
            {unresolved.map((file) => {
              const numbers = numbersFor(file);
              return (
                <div key={file.id} className="flex flex-col gap-1.5">
                  <span className="text-muted-foreground truncate font-mono text-xs" title={file.relativePath}>
                    {file.relativePath}
                  </span>
                  {/* Per-file season/episode, pre-filled from this file's name. */}
                  {isEpisodic && (
                    <div className="text-muted-foreground flex items-center gap-3">
                      <label className="flex items-center gap-1.5">
                        Season
                        <input
                          className={`${inputClass} h-7 w-16`}
                          type="number"
                          min={0}
                          value={numbers.season}
                          onChange={(e) => setNumbers(file, { season: Number(e.target.value) })}
                        />
                      </label>
                      <label className="flex items-center gap-1.5">
                        Episode
                        <input
                          className={`${inputClass} h-7 w-16`}
                          type="number"
                          min={0}
                          value={numbers.episode}
                          onChange={(e) => setNumbers(file, { episode: Number(e.target.value) })}
                        />
                      </label>
                    </div>
                  )}
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
                              season: numbers.season,
                              episode: numbers.episode,
                            })
                          }
                        >
                          {candidate.title}
                          {candidate.year ? ` (${candidate.year})` : ""} · {(candidate.score * 100).toFixed(0)}%
                        </Button>
                      ))
                    ) : (
                      <span className="text-muted-foreground text-xs">
                        {search.isPending
                          ? "Searching…"
                          : searchResults
                            ? "No matches for that title."
                            : "No candidates returned — try a corrected title above."}
                      </span>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}
