"use client";

import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { useMutation } from "@tanstack/react-query";
import { Check, Combine, FileAudio2, FileVideo2, Film, Loader2, Search } from "lucide-react";
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
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

// What Apply should do with a file. Resolved files (already mapped or skipped) default to "keep" — shown
// with their current mapping, untouched unless the operator explicitly re-decides them via Change.
type FileDecision = "keep" | "match" | "extra" | "skip";

// How an audio track relates to the mux that runs after approval: rendered inside a merge group with its
// video ("grouped"), merging into a video listed in the other section ("linked"), or matched to an
// episode/movie no video here targets — the mux stage drops such tracks ("orphan").
type MergeState = "grouped" | "linked" | "orphan" | null;

// A confirmed provider identity. Series batches resolve against one of these for the whole batch (an
// episodic torrent never mixes shows); movie batches carry one per group — a franchise pack maps each
// file group to its own movie.
interface SelectedIdentity {
  provider: string;
  providerId: string;
  title: string;
  year: number | null;
}

// A movie-catalog identity group: the files that resolve to one movie plus the group's own search state.
// Groups are pre-seeded from parsed titles when the dialog opens; the operator can move files between
// groups (or spin a file into a new one) before approving. Apply sends all groups in one match request.
interface MovieGroup {
  id: string;
  fileIds: string[];
  searchTitle: string;
  searchYear: string;
  selected: SelectedIdentity | null;
  results: MetadataCandidate[] | null;
  searching: boolean;
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
  // Movie catalogs only; empty for series (which resolve against the single `selected` identity above).
  const [movieGroups, setMovieGroups] = useState<MovieGroup[]>([]);
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

  const fileById = (fileId: string) => item.sourceFiles.find((file) => file.id === fileId);
  const groupOfFile = (fileId: string) => movieGroups.find((group) => group.fileIds.includes(fileId));

  // Per-group metadata search (movie catalogs). Plain promises rather than one useMutation: several
  // groups search in parallel when the dialog opens, each spinner and result list its own.
  //
  // Each group's searches are serialized by a token, because two can be in flight for the same group:
  // closing and re-opening the dialog re-seeds the group and fires a second auto-search while the first
  // is still running. Without the token the slower (older) response lands last and replaces the newer
  // candidates, offering movies that no longer belong to the group on screen. A response whose token is
  // no longer the group's current one is dropped, spinner included.
  const searchTokens = useRef<Record<string, number>>({});
  const runGroupSearch = (groupId: string, title: string, year: number | null) => {
    const token = (searchTokens.current[groupId] ?? 0) + 1;
    searchTokens.current[groupId] = token;
    const isCurrent = () => searchTokens.current[groupId] === token;

    setMovieGroups((prev) => prev.map((group) => (group.id === groupId ? { ...group, searching: true } : group)));
    mediaServer
      .searchIngest(item.id, { title, year, kind: "Movie" })
      .then((results) => {
        if (!isCurrent()) return;
        setMovieGroups((prev) =>
          prev.map((group) => (group.id === groupId ? { ...group, results, searching: false } : group)),
        );
      })
      .catch((error: unknown) => {
        if (!isCurrent()) return;
        setMovieGroups((prev) => prev.map((group) => (group.id === groupId ? { ...group, searching: false } : group)));
        toast.error("Search failed", { description: errorMessage(error) });
      });
  };

  // A resolved movie file re-decided to "match" must land in a group: the one already pointing at its
  // current identity when there is one, else a fresh group pre-selected to that identity.
  const ensureInGroup = (file: IngestSourceFile) => {
    if (isEpisodic) return;
    setMovieGroups((prev) => {
      if (prev.some((group) => group.fileIds.includes(file.id))) return prev;
      const assigned = file.assigned;
      const identity: SelectedIdentity | null =
        assigned?.provider && assigned.providerId
          ? { provider: assigned.provider, providerId: assigned.providerId, title: assigned.title, year: null }
          : null;
      const existing = identity ? prev.find((group) => group.selected && sameIdentity(group.selected, identity)) : undefined;
      if (existing) {
        return prev.map((group) => (group === existing ? { ...group, fileIds: [...group.fileIds, file.id] } : group));
      }
      return [
        ...prev,
        {
          id: `g-${file.id}`,
          fileIds: [file.id],
          searchTitle: (file.parsedTitle ?? "").trim(),
          searchYear: file.parsedYear != null ? String(file.parsedYear) : "",
          selected: identity,
          results: null,
          searching: false,
        },
      ];
    });
  };

  // Moves a file to another group ("new" spins it into a fresh group seeded from its parsed name).
  // Emptied groups disappear.
  const moveFileToGroup = (file: IngestSourceFile, targetGroupId: string) => {
    setMovieGroups((prev) => {
      const removed = prev.map((group) => ({ ...group, fileIds: group.fileIds.filter((id) => id !== file.id) }));
      const next =
        targetGroupId === "new"
          ? [
              ...removed,
              {
                id: `g-${file.id}-new`,
                fileIds: [file.id],
                searchTitle: (file.parsedTitle ?? "").trim(),
                searchYear: file.parsedYear != null ? String(file.parsedYear) : "",
                selected: null,
                results: null,
                searching: false,
              },
            ]
          : removed.map((group) => (group.id === targetGroupId ? { ...group, fileIds: [...group.fileIds, file.id] } : group));
      return next.filter((group) => group.fileIds.length > 0);
    });
    if (targetGroupId === "new" && (file.parsedTitle ?? "").trim()) {
      runGroupSearch(`g-${file.id}-new`, (file.parsedTitle ?? "").trim(), file.parsedYear ?? null);
    }
  };

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

      if (!isEpisodic && matchFiles.length > 0) {
        // Movie catalogs match per group — every group with match-decided files sends its own identity,
        // all in one request so the pipeline re-drives once.
        const activeGroups = movieGroups
          .map((group) => ({
            group,
            files: group.fileIds
              .map(fileById)
              .filter((file): file is IngestSourceFile => file != null && decisionFor(file) === "match"),
          }))
          .filter((entry) => entry.files.length > 0);
        if (activeGroups.some((entry) => !entry.group.selected)) {
          throw new Error("Pick a movie for every group first.");
        }
        const grouped = new Set(activeGroups.flatMap((entry) => entry.files.map((file) => file.id)));
        if (matchFiles.some((file) => !grouped.has(file.id))) {
          throw new Error("Every file to match must belong to a group.");
        }
        await mediaServer.matchIngest(item.id, {
          groups: activeGroups.map((entry) => ({
            kind: "Movie" as const,
            provider: entry.group.selected!.provider,
            providerId: entry.group.selected!.providerId,
            title: entry.group.selected!.title,
            year: entry.group.selected!.year,
            files: entry.files.map((file) => ({ sourceFileId: file.id, season: null, episode: null })),
          })),
        });
      } else if (matchFiles.length > 0 || extraFiles.length > 0) {
        if (!selected) throw new Error("Pick a series above first.");
        if (matchFiles.length > 0) {
          await mediaServer.matchIngest(item.id, {
            kind: "Episode",
            provider: selected.provider,
            providerId: selected.providerId,
            title: selected.title,
            year: selected.year,
            files: matchFiles.map((file) => ({
              sourceFileId: file.id,
              season: numbersFor(file).season,
              episode: numbersFor(file).episode,
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

    if (isEpisodic) {
      // This effect is the dialog's sanctioned reset-on-open spot (all the seeding above works the same
      // way); the grouped movie state resets with the rest.
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setMovieGroups([]);
      // No auto-identified candidates to show? Run the search up-front so variants appear without a click.
      if (item.reviewCandidates.length === 0 && parsedTitle) {
        searchMutate({ title: parsedTitle, year: parsedYear });
      }
      return;
    }

    // Movie catalogs pre-group the pending files by parsed title+year — a franchise pack parses into
    // distinct titles, one group per movie. Audio tracks join the video bucket whose title prefixes
    // theirs ("Movie One Rus" → "Movie One"), the sole video bucket when there is only one, or their own
    // bucket otherwise. A pinned identity means the operator already declared the batch one movie.
    const videoBuckets = new Map<string, { title: string; year: number | null; fileIds: string[] }>();
    const audioFiles: IngestSourceFile[] = [];
    for (const file of pendingFiles) {
      if (file.isAudio) {
        audioFiles.push(file);
        continue;
      }
      const title = (file.parsedTitle ?? "").trim();
      const key = `${title.toLowerCase()}|${file.parsedYear ?? ""}`;
      const bucket = videoBuckets.get(key);
      if (bucket) bucket.fileIds.push(file.id);
      else videoBuckets.set(key, { title, year: file.parsedYear ?? null, fileIds: [file.id] });
    }
    const buckets = [...videoBuckets.values()];
    for (const audio of audioFiles) {
      const parsed = (audio.parsedTitle ?? "").trim().toLowerCase();
      const host =
        buckets.length === 1
          ? buckets[0]
          : buckets.find((bucket) => bucket.title.length > 0 && parsed.startsWith(bucket.title.toLowerCase()));
      if (host) host.fileIds.push(audio.id);
      else buckets.push({ title: (audio.parsedTitle ?? "").trim(), year: audio.parsedYear ?? null, fileIds: [audio.id] });
    }

    const seededGroups: MovieGroup[] = pinnedIdentity
      ? [
          {
            id: "pinned",
            fileIds: pendingFiles.map((file) => file.id),
            searchTitle: pinnedIdentity.title,
            searchYear: pinnedIdentity.year != null ? String(pinnedIdentity.year) : "",
            selected: pinnedIdentity,
            results: null,
            searching: false,
          },
        ]
      : buckets.map((bucket) => ({
          id: `g-${bucket.fileIds[0]}`,
          fileIds: bucket.fileIds,
          searchTitle: bucket.title,
          searchYear: bucket.year != null ? String(bucket.year) : "",
          // A single-bucket batch mirrors the old single-identity seeding; multi-bucket groups start
          // unselected and fill from their own searches.
          selected:
            buckets.length === 1
              ? (mappedIdentity ??
                (confident && confident.score >= 0.95
                  ? { provider: confident.reference.provider, providerId: confident.reference.id, title: confident.title, year: confident.year }
                  : null))
              : null,
          results: null,
          searching: false,
        }));
    setMovieGroups(seededGroups);

    // Fetch candidates per group up-front so every group has options without a click. A lone group
    // reuses the batch's auto-identified candidates when it has some.
    for (const group of seededGroups) {
      const hasBatchCandidates = seededGroups.length === 1 && item.reviewCandidates.length > 0;
      if (!group.selected && !hasBatchCandidates && group.searchTitle) {
        runGroupSearch(group.id, group.searchTitle, group.searchYear ? Number(group.searchYear) : null);
      }
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
  // Blocked until every identity involved is picked: the batch identity for series, each group with
  // match-decided files for movies.
  const needsIdentity = isEpisodic
    ? (matchCount > 0 || extraCount > 0) && !selected
    : movieGroups.some(
        (group) =>
          !group.selected &&
          group.fileIds.some((fileId) => {
            const file = fileById(fileId);
            return file != null && decisionFor(file) === "match";
          }),
      );

  const title = item.downloadName ?? fileNameOf(item.sourceFiles[0]?.relativePath) ?? "Untitled item";

  // The mux pairing target a file's current decision points it at, or null when the decision doesn't land
  // it on a media item. `key` mirrors the MediaItemId the file resolves to after Apply — provider identity
  // plus episode/movie — which is exactly what the mux stage groups by (AudioMuxService merges confirmed
  // staged files sharing a MediaItemId); `label` is the episode part shown in a group header. Match files
  // key on the batch's selected identity — grouping them together is correct even while it's unpicked
  // (they all receive the same one on Apply) — while keep files key on their existing mapping's identity,
  // so a kept file only pairs with re-matched ones when the operator selected the series/movie it's
  // already mapped to. Already-merged tracks are done and stay out of the preview.
  const targetOf = (file: IngestSourceFile): { key: string; label: string } | null => {
    const decision = decisionFor(file);
    if (decision === "match") {
      if (!isEpisodic) {
        // Movie files key on their group's identity — files of one group pair together even before the
        // movie is picked (they all receive the same one on Apply).
        const group = groupOfFile(file.id);
        const identity = group?.selected ? `${group.selected.provider}:${group.selected.providerId}` : `group:${group?.id ?? "none"}`;
        return { key: `${identity}|Movie`, label: "Movie" };
      }
      const identity = selected ? `${selected.provider}:${selected.providerId}` : "unselected";
      const numbers = numbersFor(file);
      const label = `S${String(numbers.season).padStart(2, "0")}E${String(numbers.episode).padStart(2, "0")}`;
      return { key: `${identity}|${label}`, label };
    }
    if (decision === "keep" && file.mediaItemId != null && file.assignmentStatus !== "Merged") {
      const assigned = file.assigned;
      if (!assigned?.provider || !assigned.providerId) return null;
      const identity = `${assigned.provider}:${assigned.providerId}`;
      if (!isEpisodic) return assigned.kind === "Movie" ? { key: `${identity}|Movie`, label: "Movie" } : null;
      if (assigned.kind !== "Episode" || assigned.season == null || assigned.episode == null) return null;
      const label = `S${String(assigned.season).padStart(2, "0")}E${String(assigned.episode).padStart(2, "0")}`;
      return { key: `${identity}|${label}`, label };
    }
    return null;
  };

  // A section's rows, with audio+video files that target the same episode/movie collapsed into one merge
  // group (rendered at the first member's position). Buckets with only one kind stay as plain rows — an
  // audio-only bucket means the track has nothing to merge into.
  const rowsOf = (files: IngestSourceFile[]): (IngestSourceFile | IngestSourceFile[])[] => {
    const buckets = new Map<string, IngestSourceFile[]>();
    for (const file of files) {
      const key = targetOf(file)?.key;
      if (key == null) continue;
      const bucket = buckets.get(key);
      if (bucket) bucket.push(file);
      else buckets.set(key, [file]);
    }
    const rows: (IngestSourceFile | IngestSourceFile[])[] = [];
    const claimed = new Set<string>();
    for (const file of files) {
      if (claimed.has(file.id)) continue;
      const key = targetOf(file)?.key;
      const bucket = key != null ? buckets.get(key) : undefined;
      if (bucket && bucket.some((member) => member.isAudio) && bucket.some((member) => !member.isAudio)) {
        for (const member of bucket) claimed.add(member.id);
        rows.push(bucket);
      } else {
        rows.push(file);
      }
    }
    return rows;
  };

  // Merge state for an audio row rendered outside a group: its video may still exist in the other section
  // (pending vs. already-mapped are listed separately, so they can't share a group), or nowhere at all —
  // then the mux stage will drop the track, which the row warns about.
  const mergeStateOf = (file: IngestSourceFile): { merge: MergeState; mergeIntoName?: string } => {
    if (!file.isAudio) return { merge: null };
    const key = targetOf(file)?.key;
    if (key == null) return { merge: null };
    const video = item.sourceFiles.find((candidate) => !candidate.isAudio && targetOf(candidate)?.key === key);
    return video
      ? { merge: "linked", mergeIntoName: fileNameOf(video.relativePath) ?? video.relativePath }
      : { merge: "orphan" };
  };

  const renderRow = (file: IngestSourceFile, merge: MergeState, mergeIntoName?: string) => (
    <FileRow
      key={file.id}
      file={file}
      isEpisodic={isEpisodic}
      decision={decisionFor(file)}
      numbers={numbersFor(file)}
      busy={busy}
      merge={merge}
      mergeIntoName={mergeIntoName}
      groupPicker={groupPickerFor(file)}
      onDecision={(decision) => {
        // A movie file re-decided to "match" needs a group to resolve against (see ensureInGroup).
        if (decision === "match") ensureInGroup(file);
        setDecisions((prev) => ({ ...prev, [file.id]: decision }));
      }}
      onNumbers={(patch) => setNumbers(file, patch)}
    />
  );

  // The group mover for a pending movie file: hidden when there is nowhere to move (a lone single-file
  // group). "New group" splits a file out of a shared group.
  const groupPickerFor = (file: IngestSourceFile) => {
    if (isEpisodic || isResolved(file)) return null;
    const group = groupOfFile(file.id);
    if (!group) return null;
    const canSplit = group.fileIds.length > 1;
    if (movieGroups.length === 1 && !canSplit) return null;
    return (
      <div className="text-muted-foreground flex items-center gap-1.5 text-xs">
        <span>Group</span>
        <Select
          value={group.id}
          disabled={busy}
          onValueChange={(value) => value != null && moveFileToGroup(file, value)}
        >
          <SelectTrigger size="sm" className="h-7 w-48 text-xs">
            {/* Base UI's Value renders the raw value (the group id) without children — label it like the
                dropdown items instead. */}
            <SelectValue>
              {group.selected?.title ?? (group.searchTitle || `Movie ${movieGroups.indexOf(group) + 1}`)}
            </SelectValue>
          </SelectTrigger>
          <SelectContent>
            {movieGroups.map((candidate, index) => (
              <SelectItem key={candidate.id} value={candidate.id}>
                {candidate.selected?.title ?? (candidate.searchTitle || `Movie ${index + 1}`)}
              </SelectItem>
            ))}
            {canSplit && <SelectItem value="new">New group…</SelectItem>}
          </SelectContent>
        </Select>
      </div>
    );
  };

  const renderFileRows = (files: IngestSourceFile[]) =>
    rowsOf(files).map((row) => {
      if (!Array.isArray(row)) {
        const { merge, mergeIntoName } = mergeStateOf(row);
        return renderRow(row, merge, mergeIntoName);
      }
      // A merge group: the video(s) first, then the track(s) that mux into them, boxed together so the
      // pairing is visible before Approve. Groups follow the decisions live — retargeting an episode
      // number moves the file in or out.
      const videos = row.filter((file) => !file.isAudio);
      const audios = row.filter((file) => file.isAudio);
      return (
        <div key={row[0].id} className="flex flex-col gap-1.5 rounded-lg border border-dashed p-1.5">
          <div className="text-muted-foreground flex items-center gap-1.5 px-1 text-xs">
            <Combine className="size-3.5 shrink-0" aria-hidden="true" />
            <span>
              {isEpisodic && <span className="font-medium">{targetOf(row[0])?.label} — </span>}
              {audios.length === 1 ? "the audio track merges" : `${audios.length} audio tracks merge`} into{" "}
              {videos.length === 1 ? "the video file" : "each video file"} on import.
            </span>
          </div>
          {[...videos, ...audios].map((file) => renderRow(file, "grouped"))}
        </div>
      );
    });

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
          {/* Series: one identity for the whole batch (an episodic torrent never mixes shows), confirmed
              once here instead of per file. Movie batches pick identities inside each group below. */}
          {isEpisodic && (
            <>
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
                  <Input id={titleId} value={searchTitle} placeholder="Series title" onChange={(e) => setSearchTitle(e.target.value)} />
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

              <div className="flex shrink-0 flex-col gap-1.5">
                <span className="text-muted-foreground text-xs">Pick the series once — it applies to every file below.</span>
                <div className="flex max-h-52 flex-col gap-1.5 overflow-y-auto">
                  {identityOptions.length ? (
                    identityOptions.map((option) => (
                      <IdentityOptionButton
                        key={`${option.identity.provider}:${option.identity.providerId}`}
                        identity={option.identity}
                        posterUrl={option.posterUrl}
                        note={option.note}
                        active={selected != null && sameIdentity(selected, option.identity)}
                        disabled={busy}
                        onClick={() => setSelected(option.identity)}
                      />
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
            </>
          )}

          {/* Per-file decisions. Extras that don't exist on the provider (creditless openings, menus, …)
              can't ever match — keep them as playable extras of the series, or skip them entirely. */}
          <div className="text-muted-foreground flex items-center justify-between gap-2 text-xs">
            <span>
              {isEpisodic
                ? "Files without a match can be kept as extras of the series, or skipped (not imported)."
                : movieGroups.length > 1
                  ? "Files are grouped by movie — pick each group's movie, or move a file if the grouping is wrong. Skipped files aren’t imported."
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
            {isEpisodic
              ? renderFileRows(pendingFiles)
              : movieGroups.map((group, index) => {
                  const files = group.fileIds
                    .map(fileById)
                    .filter((file): file is IngestSourceFile => file != null);
                  if (files.length === 0) return null;
                  // A lone group reuses the batch's auto-identified candidates; otherwise each group
                  // shows its own search results.
                  const groupCandidates = group.results ?? (movieGroups.length === 1 ? item.reviewCandidates : []);
                  return (
                    <div key={group.id} className="flex flex-col gap-2 rounded-lg border p-2">
                      <div className="flex items-center gap-1.5 text-xs font-medium">
                        <Film className="text-muted-foreground size-3.5 shrink-0" aria-hidden="true" />
                        <span className="truncate">
                          {group.selected
                            ? `${group.selected.title}${group.selected.year ? ` (${group.selected.year})` : ""}`
                            : movieGroups.length > 1
                              ? `Movie ${index + 1} — pick below`
                              : "Pick the movie below"}
                        </span>
                      </div>
                      {renderFileRows(files)}

                      {/* The group's own search + candidates: confirming an identity here only affects
                          this group's files. */}
                      <form
                        className="flex flex-wrap items-end gap-2"
                        onSubmit={(e) => {
                          e.preventDefault();
                          if (group.searchTitle.trim()) {
                            runGroupSearch(group.id, group.searchTitle.trim(), group.searchYear.trim() ? Number(group.searchYear) : null);
                          }
                        }}
                      >
                        <Field className="flex-1">
                          <FieldLabel htmlFor={`${titleId}-${group.id}`}>Corrected title</FieldLabel>
                          <Input
                            id={`${titleId}-${group.id}`}
                            value={group.searchTitle}
                            placeholder="Movie title"
                            onChange={(e) =>
                              setMovieGroups((prev) =>
                                prev.map((candidate) => (candidate.id === group.id ? { ...candidate, searchTitle: e.target.value } : candidate)),
                              )
                            }
                          />
                        </Field>
                        <Field className="w-20">
                          <FieldLabel htmlFor={`${yearId}-${group.id}`}>Year</FieldLabel>
                          <Input
                            id={`${yearId}-${group.id}`}
                            type="number"
                            value={group.searchYear}
                            onChange={(e) =>
                              setMovieGroups((prev) =>
                                prev.map((candidate) => (candidate.id === group.id ? { ...candidate, searchYear: e.target.value } : candidate)),
                              )
                            }
                          />
                        </Field>
                        <Button type="submit" variant="secondary" size="sm" disabled={!group.searchTitle.trim() || group.searching}>
                          <Search />
                          {group.searching ? "Searching…" : "Search"}
                        </Button>
                      </form>
                      <div className="flex max-h-40 flex-col gap-1.5 overflow-y-auto">
                        {/* A picked identity that fell out of the candidate list (pinned, carried over
                            from a prior search) stays visible as the active option. */}
                        {group.selected != null &&
                          !groupCandidates.some(
                            (candidate) =>
                              candidate.reference.provider === group.selected!.provider &&
                              candidate.reference.id === group.selected!.providerId,
                          ) && (
                            <IdentityOptionButton
                              identity={group.selected}
                              posterUrl={null}
                              note="Selected"
                              active
                              disabled={busy}
                              onClick={() => {}}
                            />
                          )}
                        {groupCandidates.length ? (
                          groupCandidates.map((candidate) => {
                            const identity: SelectedIdentity = {
                              provider: candidate.reference.provider,
                              providerId: candidate.reference.id,
                              title: candidate.title,
                              year: candidate.year,
                            };
                            return (
                              <IdentityOptionButton
                                key={`${identity.provider}:${identity.providerId}`}
                                identity={identity}
                                posterUrl={candidate.posterUrl}
                                note={`${(candidate.score * 100).toFixed(0)}% match`}
                                active={group.selected != null && sameIdentity(group.selected, identity)}
                                disabled={busy}
                                onClick={() =>
                                  setMovieGroups((prev) =>
                                    prev.map((candidateGroup) =>
                                      candidateGroup.id === group.id ? { ...candidateGroup, selected: identity } : candidateGroup,
                                    ),
                                  )
                                }
                              />
                            );
                          })
                        ) : (
                          <span className="text-muted-foreground text-xs">
                            {group.searching
                              ? "Searching…"
                              : group.results
                                ? "No matches for that title."
                                : "No candidates — try a corrected title above."}
                          </span>
                        )}
                      </div>
                    </div>
                  );
                })}

            {resolvedFiles.length > 0 && (
              <div className="text-muted-foreground mt-1 text-xs font-medium">
                Already mapped ({resolvedFiles.length}) — verify below, Change re-decides a file.
              </div>
            )}
            {renderFileRows(resolvedFiles)}
          </div>

          {/* One Apply for the whole batch. */}
          <div className="flex shrink-0 items-center justify-between gap-2 border-t pt-3">
            <span className="text-muted-foreground min-w-0 text-xs">
              {needsIdentity
                ? isEpisodic
                  ? "Pick a series above to apply."
                  : movieGroups.length > 1
                    ? "Pick a movie for every group to apply."
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
  merge,
  mergeIntoName,
  groupPicker,
  onDecision,
  onNumbers,
}: {
  file: IngestSourceFile;
  isEpisodic: boolean;
  decision: FileDecision;
  numbers: { season: number; episode: number };
  busy: boolean;
  merge: MergeState;
  mergeIntoName?: string;
  groupPicker?: ReactNode;
  onDecision: (decision: FileDecision) => void;
  onNumbers: (patch: Partial<{ season: number; episode: number }>) => void;
}) {
  const fileName = fileNameOf(file.relativePath) ?? file.relativePath;
  const resolved = file.assignmentStatus === "Skipped" || file.mediaItemId != null;

  return (
    <div className="flex flex-col gap-1.5">
      <div className="bg-muted/60 flex min-w-0 items-start gap-2 rounded-md px-2.5 py-2" title={file.relativePath}>
        {file.isAudio ? (
          <FileAudio2 className="text-muted-foreground mt-0.5 size-4 shrink-0" aria-hidden="true" />
        ) : (
          <FileVideo2 className="text-muted-foreground mt-0.5 size-4 shrink-0" aria-hidden="true" />
        )}
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
            {isEpisodic && !file.isAudio && (
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
      {/* Mux preview for an audio row outside a merge group: where the track ends up ("linked" — its
          video is listed in the other section), or that it ends up nowhere — the mux stage drops tracks
          whose episode/movie has no video in the batch. Grouped rows say this via the group header. */}
      {file.isAudio && merge === "orphan" && (
        <span className="text-xs text-amber-600 dark:text-amber-500">
          No video file here targets the same {isEpisodic ? "episode" : "movie"} — the track has nothing to
          merge into and will be dropped.
        </span>
      )}
      {file.isAudio && merge === "linked" && (
        <span className="text-muted-foreground text-xs">
          Merges into <span className="font-medium">{mergeIntoName}</span> on import.
        </span>
      )}
      {decision !== "keep" && file.isAudio && merge == null && (
        <span className="text-muted-foreground text-xs">
          Audio track — matched to {isEpisodic ? "an episode" : "the movie"}, it merges into that video file.
        </span>
      )}

      {/* The group this movie file resolves with; moving it re-targets which movie it becomes. */}
      {decision === "match" && groupPicker}

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
      variant={active ? "default" : "ghost"}
      size="sm"
      aria-pressed={active}
      className={`h-7 px-2 ${active ? "font-semibold" : "text-muted-foreground"}`}
      disabled={disabled}
      onClick={onClick}
    >
      {label}
    </Button>
  );
}

const sameIdentity = (a: SelectedIdentity, b: SelectedIdentity) => a.provider === b.provider && a.providerId === b.providerId;

// One selectable identity row (poster, title, provenance note) — used by the series-wide identity list
// and by each movie group's candidate list.
function IdentityOptionButton({
  identity,
  posterUrl,
  note,
  active,
  disabled,
  onClick,
}: {
  identity: SelectedIdentity;
  posterUrl: string | null;
  note: string;
  active: boolean;
  disabled: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      aria-pressed={active}
      disabled={disabled}
      onClick={onClick}
      className={`hover:bg-accent focus-visible:ring-ring flex items-center gap-2.5 rounded-md border px-2 py-1.5 text-left outline-none transition-colors focus-visible:ring-2 disabled:pointer-events-none disabled:opacity-60 ${active ? "border-primary" : ""}`}
    >
      <CandidatePoster url={posterUrl} title={identity.title} />
      <span className="flex min-w-0 flex-1 flex-col">
        <span className="truncate font-medium">
          {identity.title}
          {identity.year ? ` (${identity.year})` : ""}
        </span>
        <span className="text-muted-foreground text-xs">{note}</span>
      </span>
      {active && <Check className="text-primary size-4 shrink-0" aria-hidden="true" />}
    </button>
  );
}

// The compact chip for a resolved file: where it currently points.
function mappingLabel(file: IngestSourceFile): string {
  if (file.assignmentStatus === "Skipped") return "Skipped";
  if (file.assignmentStatus === "Merged") return "Merged";
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
  if (file.assignmentStatus === "Merged") return "This audio track was merged into its video file.";
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
