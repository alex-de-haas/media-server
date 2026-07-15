"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { FileVideo2, Film, Loader2, Search } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type Catalog, type IngestItem, type IngestSourceFile, type MetadataCandidate } from "@/lib/media-server";
import { errorMessage } from "@/lib/ui";
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
 * The metadata-resolution popup for a NeedsReview ingest item. On open it pre-fills the corrected title
 * and (for series/anime) each file's season/episode from the backend's name-parsed hints, and auto-runs a
 * search when no auto-identified candidates exist — so the operator usually just picks a match. The title
 * and per-file season/episode stay editable for the cases the parser got wrong. Files that have no
 * matchable identity at all (creditless OP/EDs and other extras absent from the provider) can be kept as
 * playable extras of their series — pre-suggested from the backend's filename classification, per file or
 * in bulk against a pinned series — or skipped (per file or all at once, pre-suggested for junk like disc
 * menus) so the rest of the batch proceeds without them.
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
  const unresolved = item.sourceFiles.filter(
    (file) => file.assignmentStatus !== "Skipped" && (file.assignmentStatus === "NeedsReview" || file.mediaItemId == null),
  );

  const titleId = useId();
  const yearId = useId();
  const [searchTitle, setSearchTitle] = useState("");
  const [searchYear, setSearchYear] = useState("");
  // Per-file season/episode, keyed by source-file id and seeded from each file's parsed SxxEyy. A whole
  // season pack therefore pre-fills the right number on every file instead of a shared "1/1".
  const [episodeNumbers, setEpisodeNumbers] = useState<Record<string, { season: number; episode: number }>>({});
  // Per-file "import as extra" mode, seeded from the backend's filename classification (creditless OP/EDs
  // pre-select it; junk-leaning kinds like menus don't — Skip stays the suggested action for those). In
  // extra mode picking a series candidate attaches the file under that series' extras instead of matching
  // it to an episode. Only meaningful for episodic catalogs.
  const [extraModes, setExtraModes] = useState<Record<string, boolean>>({});
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

  // Skip = "don't import this file": for extras with no provider identity (creditless OP/EDs, menus).
  // The backend re-drives the item, so the batch proceeds once every file is matched or skipped.
  const skip = useMutation({
    mutationFn: (sourceFileIds: string[]) => mediaServer.skipIngestFiles(item.id, sourceFileIds),
    onSuccess: (_, sourceFileIds) => {
      toast.success(sourceFileIds.length === 1 ? "File skipped" : `Skipped ${sourceFileIds.length} files`, {
        description: "Skipped files won’t be imported.",
      });
      onMatched();
    },
    onError: (error) => toast.error("Couldn’t skip", { description: errorMessage(error) }),
  });

  // Attach = keep the file as a playable extra of its series (creditless OP/EDs, PVs, …): the backend
  // creates a non-episode video under the series' extras/ folder and re-drives the batch.
  const attachExtras = useMutation({
    mutationFn: (variables: {
      sourceFileIds: string[];
      provider: string;
      providerId: string;
      title: string;
      year: number | null;
    }) => mediaServer.assignIngestExtras(item.id, variables),
    onSuccess: (_, variables) => {
      toast.success(
        variables.sourceFileIds.length === 1
          ? `Extra attached to ${variables.title}`
          : `${variables.sourceFileIds.length} extras attached to ${variables.title}`,
      );
      onMatched();
    },
    onError: (error) => toast.error("Couldn’t attach extra", { description: errorMessage(error) }),
  });

  const busy = match.isPending || skip.isPending || attachExtras.isPending;

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
    // Classified extras open in extra mode (menus and other junk-leaning kinds stay off — Skip is their
    // suggested action); the operator can flip any file either way.
    setExtraModes(
      Object.fromEntries(unresolved.map((file) => [file.id, isEpisodic && file.extraKind != null && !file.extraSuggestSkip])),
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

  const isExtra = (file: IngestSourceFile) =>
    extraModes[file.id] ?? (isEpisodic && file.extraKind != null && !file.extraSuggestSkip);

  const extraFiles = unresolved.filter(isExtra);

  // A pinned series identity makes bulk-attaching all extra-mode files a one-click action; without a pin
  // the operator picks the series per file from the candidate list.
  const pinnedSeries =
    item.targetKind === "Series" && item.targetProvider && item.targetProviderId && item.targetTitle
      ? { provider: item.targetProvider, providerId: item.targetProviderId, title: item.targetTitle, year: item.targetYear }
      : null;

  const candidates = searchResults ?? item.reviewCandidates;
  const title = item.downloadName ?? fileNameOf(item.sourceFiles[0]?.relativePath) ?? "Untitled item";

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[85vh] max-w-xl">
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
            <Field className="flex-1">
              <FieldLabel htmlFor={titleId}>Corrected title</FieldLabel>
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

          {/* Extras that don't exist on the provider (creditless openings, menus, …) can't ever match —
              attach them to their series as playable extras, or skip them to unblock the rest of the batch. */}
          <div className="text-muted-foreground flex items-center justify-between gap-2 text-xs">
            <span>
              {isEpisodic
                ? "Files without a match can be kept as extras of their series, or skipped (not imported)."
                : "Files without a match can be skipped — skipped files aren’t imported."}
            </span>
            {unresolved.length > 1 && (
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="shrink-0"
                disabled={busy}
                onClick={() => skip.mutate(unresolved.map((file) => file.id))}
              >
                {skip.isPending ? "Skipping…" : `Skip all ${unresolved.length}`}
              </Button>
            )}
          </div>

          {/* One-click bulk attach when the series is already pinned — the common anime-pack case. */}
          {pinnedSeries && extraFiles.length > 0 && (
            <div className="bg-muted/40 flex items-center justify-between gap-2 rounded-md px-2.5 py-2 text-xs">
              <span className="min-w-0">
                {extraFiles.length === 1 ? "1 file looks like an extra of " : `${extraFiles.length} files look like extras of `}
                <span className="font-medium">{pinnedSeries.title}</span>.
              </span>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                className="shrink-0"
                disabled={busy}
                onClick={() =>
                  attachExtras.mutate({
                    sourceFileIds: extraFiles.map((file) => file.id),
                    provider: pinnedSeries.provider,
                    providerId: pinnedSeries.providerId,
                    title: pinnedSeries.title,
                    year: pinnedSeries.year ?? null,
                  })
                }
              >
                {attachExtras.isPending ? "Attaching…" : extraFiles.length === 1 ? "Attach as extra" : `Attach all ${extraFiles.length}`}
              </Button>
            </div>
          )}

          {/* Bounded scroll container: max-height + overflow on the same element scrolls reliably (a
              max-height on a ScrollArea root can't bound its height:100% viewport, so the list spilled out). */}
          <div className="-mr-2 flex max-h-[55vh] flex-col gap-3 overflow-y-auto pr-2">
            {unresolved.map((file) => {
              const numbers = numbersFor(file);
              const fileName = fileNameOf(file.relativePath) ?? file.relativePath;
              const extra = isExtra(file);
              return (
                <div key={file.id} className="flex flex-col gap-1.5">
                  <div className="bg-muted/60 flex min-w-0 items-start gap-2 rounded-md px-2.5 py-2" title={file.relativePath}>
                    <FileVideo2 className="text-muted-foreground mt-0.5 size-4 shrink-0" aria-hidden="true" />
                    <span className="min-w-0 flex-1 wrap-anywhere font-mono text-xs leading-relaxed font-medium">
                      {fileName}
                    </span>
                    {isEpisodic && (
                      <Button
                        type="button"
                        variant={extra ? "secondary" : "ghost"}
                        size="sm"
                        className={`-my-1 h-7 shrink-0 px-2 ${extra ? "" : "text-muted-foreground"}`}
                        title={extra ? "Will be kept as a playable extra of the series — click to match an episode instead" : "Keep as a playable extra of the series instead of matching an episode"}
                        disabled={busy}
                        onClick={() => setExtraModes((prev) => ({ ...prev, [file.id]: !extra }))}
                      >
                        Extra
                      </Button>
                    )}
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      className="text-muted-foreground -my-1 h-7 shrink-0 px-2"
                      title="Don’t import this file"
                      disabled={busy}
                      onClick={() => skip.mutate([file.id])}
                    >
                      Skip
                    </Button>
                  </div>
                  {/* Classification verdict: what the file looks like and what the pre-suggested action is. */}
                  {isEpisodic && file.extraKind != null && (
                    <span className="text-muted-foreground text-xs">
                      Looks like <span className="font-medium">{file.extraTitle ?? "an extra"}</span>
                      {file.extraSuggestSkip
                        ? " — usually junk; Skip is suggested, or keep it via Extra."
                        : extra
                          ? " — pick a series below to attach it under that series’ extras."
                          : "."}
                    </span>
                  )}
                  {extra && file.extraKind == null && (
                    <span className="text-muted-foreground text-xs">
                      Will be kept as an extra — pick a series below to attach it to.
                    </span>
                  )}
                  {/* Per-file season/episode, pre-filled from this file's name. */}
                  {isEpisodic && !extra && (
                    <div className="text-muted-foreground flex items-center gap-3">
                      <label className="flex items-center gap-1.5">
                        Season
                        <Input
                          className="w-16"
                          type="number"
                          min={0}
                          value={numbers.season}
                          onChange={(e) => setNumbers(file, { season: Number(e.target.value) })}
                        />
                      </label>
                      <label className="flex items-center gap-1.5">
                        Episode
                        <Input
                          className="w-16"
                          type="number"
                          min={0}
                          value={numbers.episode}
                          onChange={(e) => setNumbers(file, { episode: Number(e.target.value) })}
                        />
                      </label>
                    </div>
                  )}
                  <div className="flex flex-col gap-1.5">
                    {candidates.length ? (
                      candidates.map((candidate) => {
                        const matching =
                          (match.isPending &&
                            match.variables?.sourceFileId === file.id &&
                            match.variables?.providerId === candidate.reference.id) ||
                          (attachExtras.isPending &&
                            attachExtras.variables?.sourceFileIds.length === 1 &&
                            attachExtras.variables?.sourceFileIds[0] === file.id &&
                            attachExtras.variables?.providerId === candidate.reference.id);
                        return (
                          <button
                            key={`${candidate.reference.provider}:${candidate.reference.id}`}
                            type="button"
                            disabled={busy}
                            onClick={() =>
                              extra
                                ? attachExtras.mutate({
                                    sourceFileIds: [file.id],
                                    provider: candidate.reference.provider,
                                    providerId: candidate.reference.id,
                                    title: candidate.title,
                                    year: candidate.year,
                                  })
                                : match.mutate({
                                    sourceFileId: file.id,
                                    provider: candidate.reference.provider,
                                    providerId: candidate.reference.id,
                                    title: candidate.title,
                                    year: candidate.year,
                                    season: numbers.season,
                                    episode: numbers.episode,
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
                            {matching && <Loader2 className="size-4 shrink-0 animate-spin" />}
                          </button>
                        );
                      })
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

// Source paths include the private staging prefix and can be much longer than the dialog. The basename is
// the operator-facing identity here: it keeps episode markers and release suffixes visible for every row.
function fileNameOf(relativePath: string | undefined): string | null {
  const normalized = relativePath?.replace(/\\/g, "/").replace(/\/+$/, "");
  if (!normalized) return null;
  return normalized.slice(normalized.lastIndexOf("/") + 1);
}

// 2:3 poster thumbnail for a candidate, with a neutral placeholder when the provider returned no poster
// (or the image fails to load), so every row keeps the same shape. Shared with the pin dialog.
export function CandidatePoster({ url, title }: { url: string | null; title: string }) {
  const [failed, setFailed] = useState(false);
  // Reset the load-error state if this instance is reused for a different candidate (React reconciliation),
  // otherwise a prior failure would wrongly keep showing the placeholder for the new poster.
  const [lastUrl, setLastUrl] = useState(url);
  if (url !== lastUrl) {
    setLastUrl(url);
    setFailed(false);
  }

  if (!url || failed) {
    return (
      <div className="bg-muted text-muted-foreground flex aspect-[2/3] w-10 shrink-0 items-center justify-center rounded">
        <Film className="size-4" />
      </div>
    );
  }
  return (
    // eslint-disable-next-line @next/next/no-img-element
    <img
      src={url}
      alt={title}
      loading="lazy"
      onError={() => setFailed(true)}
      className="aspect-[2/3] w-10 shrink-0 rounded object-cover"
    />
  );
}
