"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { Loader2, Search, Target, TriangleAlert } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type Catalog, type IngestItem, type MetadataCandidate } from "@/lib/media-server";
import { errorMessage } from "@/lib/ui";
import { CandidatePoster } from "@/components/ingest-review-dialog";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Field, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";

/**
 * Pins a target identity on an item before/while it downloads: the operator searches by the correct title
 * and picks the movie/series, which is stored so Identify resolves straight to it after the download — no
 * name-parse guess, no getting stuck at review. For a series the pick is the show; per-file season/episode
 * still come from the file names at identify time. Re-openable to change the pin.
 */
export function IngestPinDialog({
  item,
  catalog,
  open,
  onOpenChange,
  onPinned,
}: {
  item: IngestItem;
  catalog: Catalog | undefined;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onPinned: () => void;
}) {
  const isEpisodic = catalog?.type === "Series" || catalog?.type === "Anime";
  const kind = isEpisodic ? "Series" : "Movie";

  const titleId = useId();
  const yearId = useId();
  const [searchTitle, setSearchTitle] = useState("");
  const [searchYear, setSearchYear] = useState("");
  const [searchResults, setSearchResults] = useState<MetadataCandidate[] | null>(null);

  const search = useMutation({
    mutationFn: (variables: { title: string; year: number | null }) =>
      mediaServer.searchIngest(item.id, { title: variables.title, year: variables.year, kind }),
    onSuccess: (results) => setSearchResults(results),
    onError: (error) => toast.error("Search failed", { description: errorMessage(error) }),
  });

  const pin = useMutation({
    mutationFn: (variables: { provider: string; providerId: string; title: string; year: number | null }) =>
      mediaServer.pinIngest(item.id, {
        provider: variables.provider,
        providerId: variables.providerId,
        kind,
        title: variables.title,
        year: variables.year,
      }),
    onSuccess: () => {
      toast.success(isEpisodic ? "Series pinned" : "Title pinned");
      onPinned();
    },
    onError: (error) => toast.error("Couldn’t pin title", { description: errorMessage(error) }),
  });

  // Seed the search on open: a title the operator already pinned, else the backend's parsed title/year, and
  // auto-run the search so candidates appear without a first click. Keyed on item.id → one seed per open.
  const searchMutate = search.mutate;
  const seededFor = useRef<string | null>(null);
  useEffect(() => {
    if (!open) {
      seededFor.current = null;
      return;
    }
    if (seededFor.current === item.id) return;
    seededFor.current = item.id;

    const parsedFile = item.sourceFiles.find((file) => file.parsedTitle?.trim());
    const seedTitle = (item.targetTitle ?? parsedFile?.parsedTitle ?? item.downloadName ?? "").trim();
    const seedYear = item.targetYear ?? parsedFile?.parsedYear ?? null;
    setSearchTitle(seedTitle);
    setSearchYear(seedYear != null ? String(seedYear) : "");
    setSearchResults(null);
    if (seedTitle) {
      searchMutate({ title: seedTitle, year: seedYear });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, item.id]);

  const candidates = searchResults ?? [];
  const heading = item.downloadName ?? item.sourceFiles[0]?.relativePath ?? "Untitled item";
  const pinnedId = item.targetProviderId;

  // A movie pin applies to every video in the batch, so a franchise pack would import as one movie with
  // N versions rather than N movies. Warn before that happens — the auto-identify path (or the review
  // dialog's per-group matching) is what handles a multi-movie pack. Series packs are the normal case:
  // the pin is the show and each file still resolves to its own episode.
  const videoCount = item.sourceFiles.filter((file) => !file.isAudio).length;
  const warnMultiVideo = !isEpisodic && videoCount > 1;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[85vh] max-w-xl">
        <DialogHeader>
          <DialogTitle>{isEpisodic ? "Set series" : "Set title"}</DialogTitle>
          <DialogDescription className="truncate" title={heading}>
            {heading}
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-3 text-sm">
          <p className="text-muted-foreground text-xs">
            {isEpisodic
              ? "Pick the series now and the download will be identified as it — episodes are matched by their file names."
              : "Pick the movie now and the download will be identified as it — no waiting to fix the name later."}
          </p>

          {warnMultiVideo && (
            <p className="flex items-start gap-1.5 text-xs text-amber-600 dark:text-amber-500">
              <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
              <span>
                This download has {videoCount} video files. Pinning one movie imports all of them as versions of
                that movie. For a pack of different films, leave it unpinned — each file is identified on its own,
                and Resolve match lets you set a movie per file.
              </span>
            </p>
          )}

          <form
            className="flex flex-wrap items-end gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              const trimmed = searchTitle.trim();
              if (!trimmed) return;
              // Number.parseInt yields NaN for empty/garbage input; send null rather than a NaN year.
              const year = Number.parseInt(searchYear, 10);
              search.mutate({ title: trimmed, year: Number.isNaN(year) ? null : year });
            }}
          >
            <Field className="flex-1">
              <FieldLabel htmlFor={titleId}>{isEpisodic ? "Series title" : "Movie title"}</FieldLabel>
              <Input
                id={titleId}
                value={searchTitle}
                placeholder={isEpisodic ? "Series title" : "Movie title"}
                onChange={(e) => setSearchTitle(e.target.value)}
              />
            </Field>
            <Field className="w-20">
              <FieldLabel htmlFor={yearId}>Year</FieldLabel>
              <Input id={yearId} type="number" value={searchYear} onChange={(e) => setSearchYear(e.target.value)} />
            </Field>
            <Button type="submit" variant="secondary" size="sm" disabled={!searchTitle.trim() || search.isPending}>
              <Search />
              {search.isPending ? "Searching…" : "Search"}
            </Button>
          </form>

          <div className="-mr-2 flex max-h-[55vh] flex-col gap-1.5 overflow-y-auto pr-2">
            {candidates.length ? (
              candidates.map((candidate) => {
                const isPinned = candidate.reference.id === pinnedId && candidate.reference.provider === item.targetProvider;
                const pinning = pin.isPending && pin.variables?.providerId === candidate.reference.id;
                return (
                  <button
                    key={`${candidate.reference.provider}:${candidate.reference.id}`}
                    type="button"
                    disabled={pin.isPending}
                    onClick={() =>
                      pin.mutate({
                        provider: candidate.reference.provider,
                        providerId: candidate.reference.id,
                        title: candidate.title,
                        year: candidate.year,
                      })
                    }
                    className="hover:bg-accent focus-visible:ring-ring flex items-center gap-2.5 rounded-md border px-2 py-1.5 text-left outline-none transition-colors focus-visible:ring-2 disabled:pointer-events-none disabled:opacity-60"
                  >
                    <CandidatePoster url={candidate.posterUrl} title={candidate.title} />
                    <span className="flex min-w-0 flex-1 flex-col">
                      <span className="truncate font-medium">
                        {candidate.title}
                        {candidate.year ? ` (${candidate.year})` : ""}
                      </span>
                      <span className="text-muted-foreground text-xs">{(candidate.score * 100).toFixed(0)}% match</span>
                    </span>
                    {pinning ? (
                      <Loader2 className="size-4 shrink-0 animate-spin" />
                    ) : (
                      isPinned && <Target className="text-primary size-4 shrink-0" aria-label="Pinned" />
                    )}
                  </button>
                );
              })
            ) : (
              <span className="text-muted-foreground text-xs">
                {search.isPending
                  ? "Searching…"
                  : searchResults
                    ? "No matches for that title."
                    : "Type a title above and search."}
              </span>
            )}
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}
