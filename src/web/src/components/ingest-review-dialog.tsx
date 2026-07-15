"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { Check, FileVideo2, Film, Loader2, Search } from "lucide-react";
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

// What Apply should do with a file. Resolved files (already mapped or skipped) default to "keep" — shown
// with their current mapping, untouched unless the operator explicitly re-decides them via Change.
type FileDecision = "keep" | "match" | "extra" | "skip";

// The one identity the whole batch resolves against — a torrent never mixes titles, so the operator
// confirms the series (or movie) once and only the per-file season/episode vary.
interface SelectedIdentity {
  provider: string;
  providerId: string;
  title: string;
  year: number | null;
}

/**
 * The metadata-resolution popup for a NeedsReview ingest item. The identity is confirmed once for the
 * whole batch: the operator picks the series (or movie) at the top — pre-selected from a pinned target,
 * the auto-matched files' series, or a high-confidence candidate — and every file below only carries its
 * own decision (episode with season/episode numbers, series extra, or skip) plus one Apply. Files the
 * pipeline already mapped stay visible with their current mapping (e.g. "S01E05") so the operator can
 * verify them and re-decide any of them via Change while the batch is still in review (nothing has been
 * organized yet). Classified extras pre-select the extra decision; junk-leaning kinds pre-select skip.
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

  // A file is resolved once it's mapped (Confirmed with a media item) or skipped; everything else still
  // needs a decision. Resolved files stay listed so the operator can verify and re-decide them.
  const isResolved = (file: IngestSourceFile) => file.assignmentStatus === "Skipped" || file.mediaItemId != null;
  const pendingFiles = item.sourceFiles.filter((file) => !isResolved(file));
  const resolvedFiles = item.sourceFiles.filter(isResolved);

  const titleId = useId();
  const yearId = useId();
  const [searchTitle, setSearchTitle] = useState("");
  const [searchYear, setSearchYear] = useState("");
  const [selected, setSelected] = useState<SelectedIdentity | null>(null);
  // Per-file overrides of the default decision (pending files: match/extra/skip from the backend's
  // classification; resolved files: keep). Only explicit operator choices live here.
  const [decisions, setDecisions] = useState<Record<string, FileDecision>>({});
  // Per-file season/episode, keyed by source-file id and seeded from the file's current mapping (when
  // re-deciding a mapped file) or its parsed SxxEyy, so a season pack pre-fills the right number everywhere.
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

  const decisionFor = (file: IngestSourceFile): FileDecision => {
    const explicit = decisions[file.id];
    if (explicit) return explicit;
    if (isResolved(file)) return "keep";
    if (isEpisodic && file.extraKind != null) return file.extraSuggestSkip ? "skip" : "extra";
    return "match";
  };

  const numbersFor = (file: IngestSourceFile) =>
    episodeNumbers[file.id] ??
    (file.assigned?.kind === "Episode"
      ? { season: file.assigned.season ?? 1, episode: file.assigned.episode ?? 1 }
      : { season: file.parsedSeason ?? 1, episode: file.parsedEpisode ?? 1 });

  const setNumbers = (file: IngestSourceFile, patch: Partial<{ season: number; episode: number }>) =>
    setEpisodeNumbers((prev) => ({ ...prev, [file.id]: { ...numbersFor(file), ...patch } }));

  // Apply executes every non-keep decision against the one selected identity: a bulk match for episode
  // (or movie) files, one extras attach, one skip. Each call re-drives the item; once everything is
  // matched or skipped the batch proceeds.
  const apply = useMutation({
    mutationFn: async () => {
      const matchFiles = item.sourceFiles.filter((file) => decisionFor(file) === "match");
      const extraFiles = item.sourceFiles.filter((file) => decisionFor(file) === "extra");
      const skipFiles = item.sourceFiles.filter(
        (file) => decisionFor(file) === "skip" && file.assignmentStatus !== "Skipped",
      );

      if (matchFiles.length > 0 || extraFiles.length > 0) {
        if (!selected) throw new Error(isEpisodic ? "Pick a series above first." : "Pick a movie above first.");
        if (matchFiles.length > 0) {
          await mediaServer.matchIngest(item.id, {
            kind: isEpisodic ? "Episode" : "Movie",
            provider: selected.provider,
            providerId: selected.providerId,
            title: selected.title,
            year: selected.year,
            files: matchFiles.map((file) => ({
              sourceFileId: file.id,
              season: isEpisodic ? numbersFor(file).season : null,
              episode: isEpisodic ? numbersFor(file).episode : null,
            })),
          });
        }
        if (extraFiles.length > 0) {
          await mediaServer.assignIngestExtras(item.id, {
            sourceFileIds: extraFiles.map((file) => file.id),
            provider: selected.provider,
            providerId: selected.providerId,
            title: selected.title,
            year: selected.year,
          });
        }
      }
      if (skipFiles.length > 0) {
        await mediaServer.skipIngestFiles(item.id, skipFiles.map((file) => file.id));
      }

      return { matched: matchFiles.length, extras: extraFiles.length, skipped: skipFiles.length };
    },
    onSuccess: (result) => {
      const parts = [
        result.matched > 0 ? `${result.matched} matched` : null,
        result.extras > 0 ? `${result.extras} kept as extras` : null,
        result.skipped > 0 ? `${result.skipped} skipped` : null,
      ].filter(Boolean);
      toast.success("Changes applied", { description: parts.join(", ") });
      onMatched();
    },
    onError: (error) => toast.error("Couldn’t apply changes", { description: errorMessage(error) }),
  });

  const busy = apply.isPending;

  // The identity the pipeline already resolved (some of) the batch against — an episode's provider
  // reference is its series — so re-opening a half-matched pack pre-selects the same series.
  const mappedIdentity = ((): SelectedIdentity | null => {
    const file = item.sourceFiles.find((candidate) => candidate.assigned?.provider && candidate.assigned.providerId);
    return file?.assigned
      ? { provider: file.assigned.provider!, providerId: file.assigned.providerId!, title: file.assigned.seriesTitle ?? file.assigned.title, year: null }
      : null;
  })();

  const pinnedIdentity: SelectedIdentity | null =
    item.targetProvider && item.targetProviderId && item.targetTitle
      ? { provider: item.targetProvider, providerId: item.targetProviderId, title: item.targetTitle, year: item.targetYear }
      : null;

  // Re-seed whenever the dialog opens for an item: corrected title/year from the first undecided file
  // (the series title for packs), per-file numbers/decisions back to their defaults, and the selected
  // identity from the pin, the already-mapped series, or a high-confidence candidate.
  const searchMutate = search.mutate;
  const seededFor = useRef<string | null>(null);
  useEffect(() => {
    if (!open) {
      seededFor.current = null;
      return;
    }
    // Key the guard on the catalog kind too: if the dialog opens before the catalog query resolves,
    // isEpisodic starts false and the decisions (and search kind) seed as if for a movie — re-seed once
    // the catalog arrives instead of staying stale.
    const seedKey = `${item.id}:${isEpisodic}`;
    if (seededFor.current === seedKey) return;
    seededFor.current = seedKey;

    const first = pendingFiles[0] ?? item.sourceFiles[0];
    const parsedTitle = first?.parsedTitle?.trim() ?? "";
    const parsedYear = first?.parsedYear ?? null;
    setSearchTitle(parsedTitle);
    setSearchYear(parsedYear != null ? String(parsedYear) : "");
    setSearchResults(null);
    setDecisions({});
    setEpisodeNumbers({});

    const confident = item.reviewCandidates[0];
    setSelected(
      pinnedIdentity ??
        mappedIdentity ??
        (confident && confident.score >= 0.95
          ? { provider: confident.reference.provider, providerId: confident.reference.id, title: confident.title, year: confident.year }
          : null),
    );

    // No auto-identified candidates to show? Run the search up-front so variants appear without a click.
    if (item.reviewCandidates.length === 0 && parsedTitle) {
      searchMutate({ title: parsedTitle, year: parsedYear });
    }
    // Everything read here is derived from item each render; keying the effect on item.id (+ the catalog
    // kind via isEpisodic) keeps it to one run per open.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, item.id, isEpisodic]);

  // The selectable identities: pinned target and already-mapped series first (deduplicated), then the
  // search/auto candidates. Selecting any of them is local — nothing is sent until Apply.
  const candidates = searchResults ?? item.reviewCandidates;
  const identityOptions: { identity: SelectedIdentity; posterUrl: string | null; note: string }[] = [];
  const addOption = (identity: SelectedIdentity | null, posterUrl: string | null, note: string) => {
    if (!identity) return;
    if (identityOptions.some((option) => sameIdentity(option.identity, identity))) return;
    identityOptions.push({ identity, posterUrl, note });
  };
  addOption(pinnedIdentity, null, "Pinned");
  addOption(mappedIdentity, null, "Current match");
  for (const candidate of candidates) {
    addOption(
      { provider: candidate.reference.provider, providerId: candidate.reference.id, title: candidate.title, year: candidate.year },
      candidate.posterUrl,
      `${(candidate.score * 100).toFixed(0)}% match`,
    );
  }

  const matchCount = item.sourceFiles.filter((file) => decisionFor(file) === "match").length;
  const extraCount = item.sourceFiles.filter((file) => decisionFor(file) === "extra").length;
  const skipCount = item.sourceFiles.filter(
    (file) => decisionFor(file) === "skip" && file.assignmentStatus !== "Skipped",
  ).length;
  const changeCount = matchCount + extraCount + skipCount;
  const needsIdentity = (matchCount > 0 || extraCount > 0) && !selected;

  const title = item.downloadName ?? fileNameOf(item.sourceFiles[0]?.relativePath) ?? "Untitled item";

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[85vh] max-w-xl overflow-hidden">
        <DialogHeader className="shrink-0">
          <DialogTitle>Resolve match</DialogTitle>
          <DialogDescription className="truncate" title={title}>
            {title}
          </DialogDescription>
        </DialogHeader>

        {/* min-h-0 lets the file list flex-shrink inside the height-capped dialog, keeping the Apply
            footer pinned inside the card instead of overflowing past it. */}
        <div className="flex min-h-0 flex-col gap-3 text-sm">
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

          {/* One identity for the whole batch — a torrent never mixes titles, so the series (or movie) is
              confirmed once here instead of per file. */}
          <div className="flex shrink-0 flex-col gap-1.5">
            <span className="text-muted-foreground text-xs">
              {isEpisodic
                ? "Pick the series once — it applies to every file below."
                : "Pick the movie once — it applies to every file below."}
            </span>
            <div className="flex max-h-52 flex-col gap-1.5 overflow-y-auto">
              {identityOptions.length ? (
                identityOptions.map((option) => {
                  const active = selected != null && sameIdentity(selected, option.identity);
                  return (
                    <button
                      key={`${option.identity.provider}:${option.identity.providerId}`}
                      type="button"
                      aria-pressed={active}
                      disabled={busy}
                      onClick={() => setSelected(option.identity)}
                      className={`hover:bg-accent focus-visible:ring-ring flex items-center gap-2.5 rounded-md border px-2 py-1.5 text-left outline-none transition-colors focus-visible:ring-2 disabled:pointer-events-none disabled:opacity-60 ${active ? "border-primary" : ""}`}
                    >
                      <CandidatePoster url={option.posterUrl} title={option.identity.title} />
                      <span className="flex min-w-0 flex-1 flex-col">
                        <span className="truncate font-medium">
                          {option.identity.title}
                          {option.identity.year ? ` (${option.identity.year})` : ""}
                        </span>
                        <span className="text-muted-foreground text-xs">{option.note}</span>
                      </span>
                      {active && <Check className="text-primary size-4 shrink-0" aria-hidden="true" />}
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

          {/* Per-file decisions. Extras that don't exist on the provider (creditless openings, menus, …)
              can't ever match — keep them as playable extras of the series, or skip them entirely. */}
          <div className="text-muted-foreground flex items-center justify-between gap-2 text-xs">
            <span>
              {isEpisodic
                ? "Files without a match can be kept as extras of the series, or skipped (not imported)."
                : "Files without a match can be skipped — skipped files aren’t imported."}
            </span>
            {pendingFiles.length > 1 && (
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="shrink-0"
                disabled={busy}
                onClick={() =>
                  setDecisions((prev) => ({
                    ...prev,
                    ...Object.fromEntries(pendingFiles.map((file) => [file.id, "skip" as const])),
                  }))
                }
              >
                Skip all {pendingFiles.length}
              </Button>
            )}
          </div>

          {/* The flexing scroll region: it takes whatever height the capped dialog has left (min-h-0 up
              the tree makes that boundable) and scrolls its overflow, so the footer below never spills. */}
          <div className="-mr-2 flex min-h-20 flex-1 flex-col gap-2 overflow-y-auto pr-2">
            {pendingFiles.map((file) => (
              <FileRow
                key={file.id}
                file={file}
                isEpisodic={isEpisodic}
                decision={decisionFor(file)}
                numbers={numbersFor(file)}
                busy={busy}
                onDecision={(decision) => setDecisions((prev) => ({ ...prev, [file.id]: decision }))}
                onNumbers={(patch) => setNumbers(file, patch)}
              />
            ))}

            {resolvedFiles.length > 0 && (
              <div className="text-muted-foreground mt-1 text-xs font-medium">
                Already mapped ({resolvedFiles.length}) — verify below, Change re-decides a file.
              </div>
            )}
            {resolvedFiles.map((file) => (
              <FileRow
                key={file.id}
                file={file}
                isEpisodic={isEpisodic}
                decision={decisionFor(file)}
                numbers={numbersFor(file)}
                busy={busy}
                onDecision={(decision) => setDecisions((prev) => ({ ...prev, [file.id]: decision }))}
                onNumbers={(patch) => setNumbers(file, patch)}
              />
            ))}
          </div>

          {/* One Apply for the whole batch. */}
          <div className="flex shrink-0 items-center justify-between gap-2 border-t pt-3">
            <span className="text-muted-foreground min-w-0 text-xs">
              {needsIdentity
                ? isEpisodic
                  ? "Pick a series above to apply."
                  : "Pick a movie above to apply."
                : changeCount === 0
                  ? "No pending changes."
                  : [
                      matchCount > 0 ? `${matchCount} to match` : null,
                      extraCount > 0 ? `${extraCount} as extras` : null,
                      skipCount > 0 ? `${skipCount} to skip` : null,
                    ]
                      .filter(Boolean)
                      .join(" · ")}
            </span>
            <Button type="button" disabled={busy || changeCount === 0 || needsIdentity} onClick={() => apply.mutate()}>
              {busy && <Loader2 className="animate-spin" />}
              {busy ? "Approving…" : changeCount > 0 ? `Approve (${changeCount})` : "Approve"}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

/**
 * One source file: its name, the current mapping when it's already resolved (with Change to re-decide),
 * or the decision buttons (Episode/Extra/Skip) with season/episode inputs while it needs one.
 */
function FileRow({
  file,
  isEpisodic,
  decision,
  numbers,
  busy,
  onDecision,
  onNumbers,
}: {
  file: IngestSourceFile;
  isEpisodic: boolean;
  decision: FileDecision;
  numbers: { season: number; episode: number };
  busy: boolean;
  onDecision: (decision: FileDecision) => void;
  onNumbers: (patch: Partial<{ season: number; episode: number }>) => void;
}) {
  const fileName = fileNameOf(file.relativePath) ?? file.relativePath;
  const resolved = file.assignmentStatus === "Skipped" || file.mediaItemId != null;

  return (
    <div className="flex flex-col gap-1.5">
      <div className="bg-muted/60 flex min-w-0 items-start gap-2 rounded-md px-2.5 py-2" title={file.relativePath}>
        <FileVideo2 className="text-muted-foreground mt-0.5 size-4 shrink-0" aria-hidden="true" />
        <span className="min-w-0 flex-1 wrap-anywhere font-mono text-xs leading-relaxed font-medium">{fileName}</span>

        {decision === "keep" ? (
          <>
            <span
              className="bg-muted text-muted-foreground -my-0.5 shrink-0 rounded px-1.5 py-0.5 text-xs font-medium"
              title={mappingTooltip(file)}
            >
              {mappingLabel(file)}
            </span>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              className="text-muted-foreground -my-1 h-7 shrink-0 px-2"
              title="Re-decide this file (re-match, keep as extra, or skip)"
              disabled={busy}
              // Seed from the current mapping — a skipped file opens with Skip active (no fake pending
              // change), an extra with Extra, an episode/movie with the match editor.
              onClick={() =>
                onDecision(
                  file.assignmentStatus === "Skipped" ? "skip" : file.assigned?.kind === "Video" ? "extra" : "match",
                )
              }
            >
              Change
            </Button>
          </>
        ) : (
          <div className="-my-1 flex shrink-0 items-center gap-0.5">
            <DecisionButton
              label={isEpisodic ? "Episode" : "Movie"}
              active={decision === "match"}
              disabled={busy}
              onClick={() => onDecision("match")}
            />
            {isEpisodic && (
              <DecisionButton label="Extra" active={decision === "extra"} disabled={busy} onClick={() => onDecision("extra")} />
            )}
            <DecisionButton label="Skip" active={decision === "skip"} disabled={busy} onClick={() => onDecision("skip")} />
            {resolved && (
              <DecisionButton label="Keep" active={false} disabled={busy} onClick={() => onDecision("keep")} />
            )}
          </div>
        )}
      </div>

      {/* Classification verdict: what the file looks like and why its decision was pre-selected. */}
      {decision !== "keep" && file.extraKind != null && (
        <span className="text-muted-foreground text-xs">
          Looks like <span className="font-medium">{file.extraTitle ?? "an extra"}</span>
          {file.extraSuggestSkip ? " — usually junk; Skip is suggested." : "."}
        </span>
      )}

      {/* Per-file season/episode, pre-filled from the current mapping or this file's parsed name. */}
      {decision === "match" && isEpisodic && (
        <div className="text-muted-foreground flex items-center gap-3">
          <label className="flex items-center gap-1.5">
            Season
            <Input
              className="w-16"
              type="number"
              min={0}
              value={numbers.season}
              onChange={(e) => onNumbers({ season: Number(e.target.value) })}
            />
          </label>
          <label className="flex items-center gap-1.5">
            Episode
            <Input
              className="w-16"
              type="number"
              min={0}
              value={numbers.episode}
              onChange={(e) => onNumbers({ episode: Number(e.target.value) })}
            />
          </label>
        </div>
      )}
    </div>
  );
}

function DecisionButton({
  label,
  active,
  disabled,
  onClick,
}: {
  label: string;
  active: boolean;
  disabled: boolean;
  onClick: () => void;
}) {
  return (
    <Button
      type="button"
      variant={active ? "secondary" : "ghost"}
      size="sm"
      aria-pressed={active}
      className={`h-7 px-2 ${active ? "" : "text-muted-foreground"}`}
      disabled={disabled}
      onClick={onClick}
    >
      {label}
    </Button>
  );
}

const sameIdentity = (a: SelectedIdentity, b: SelectedIdentity) => a.provider === b.provider && a.providerId === b.providerId;

// The compact chip for a resolved file: where it currently points.
function mappingLabel(file: IngestSourceFile): string {
  if (file.assignmentStatus === "Skipped") return "Skipped";
  const assigned = file.assigned;
  if (!assigned) return "Mapped";
  if (assigned.kind === "Episode" && assigned.season != null && assigned.episode != null) {
    return `S${String(assigned.season).padStart(2, "0")}E${String(assigned.episode).padStart(2, "0")}`;
  }
  if (assigned.kind === "Video") return "Extra";
  return assigned.title;
}

function mappingTooltip(file: IngestSourceFile): string {
  if (file.assignmentStatus === "Skipped") return "This file won’t be imported.";
  const assigned = file.assigned;
  if (!assigned) return "Mapped";
  return [assigned.seriesTitle, assigned.title].filter(Boolean).join(" · ");
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
