"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, ArrowDownToLine, ArrowUpFromLine, RefreshCw } from "lucide-react";
import {
  mediaServer,
  type WatchHistorySyncClassification,
  type WatchHistorySyncPreview,
  type WatchHistorySyncResult,
  type WatchHistorySyncSkip,
  type FavoritesSyncPlan,
  type FavoriteSyncAction,
} from "@/lib/media-server";
import { buildScopeRequest, canApply, canPreview, changeCount, hasBlockingWork } from "@/lib/watch-history";
import { errorMessage } from "@/lib/ui";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Spinner } from "@/components/ui/spinner";

// The classifications worth a line in the preview, in the order a reader wants them: what will move
// first, then what is already settled, then what was set aside and why.
const CLASSIFICATIONS: Array<{
  key: WatchHistorySyncClassification;
  label: (provider: string) => string;
  hint: (provider: string) => string;
}> = [
  {
    key: "RemoteOnly",
    label: (p) => `Import from ${p}`,
    hint: (p) => `Watched on ${p} but not here.`,
  },
  {
    key: "LocalOnly",
    label: (p) => `Send to ${p}`,
    hint: (p) => `Watched here but unknown to ${p}.`,
  },
  { key: "InSync", label: () => "Already in sync", hint: () => "Watched on both sides." },
  {
    key: "LocalUnwatchedWithHistory",
    label: () => "Left unwatched",
    hint: () => "Unwatched here on purpose; not exported.",
  },
  {
    key: "AmbiguousLocalIdentity",
    label: () => "Ambiguous copies",
    hint: () => "Several copies share one identity; skipped.",
  },
  {
    key: "UnidentifiedLocally",
    label: () => "Unmatched",
    hint: () => "No provider identity; skipped.",
  },
];

const SKIP_LABELS: Record<WatchHistorySyncSkip, string> = {
  LocalStateChangedDuringSync: "changed during sync",
  AmbiguousLocalIdentity: "ambiguous copies",
  UnidentifiedLocally: "unmatched",
  ExportFailed: "export failed",
};

type Phase =
  | { kind: "scope" }
  | { kind: "previewing" }
  // Favorites ride along with the history preview; null when the provider carries none (or the
  // favorites read failed, which must not sink the history comparison the user asked for).
  | { kind: "preview"; preview: WatchHistorySyncPreview; favorites: FavoritesSyncPlan | null }
  | { kind: "applying" }
  | { kind: "result"; result: WatchHistorySyncResult }
  | { kind: "error"; message: string };

/**
 * The Sync with {provider} popup. It first builds a read-only preview — nothing is written until the
 * user approves it — then applies that one run. Apply is held back when local changes are still on
 * their way to the provider, because importing the provider's older snapshot would undo them.
 */
export function WatchHistorySyncDialog({
  providerKey,
  providerName,
  supportsFavorites = false,
  open,
  onOpenChange,
  onApplied,
}: {
  providerKey: string;
  providerName: string;
  /** Whether this provider carries favorites; false hides the favorites half entirely. */
  supportsFavorites?: boolean;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onApplied: () => void;
}) {
  const [phase, setPhase] = useState<Phase>({ kind: "scope" });
  const [includeMovies, setIncludeMovies] = useState(true);
  const [includeEpisodes, setIncludeEpisodes] = useState(true);
  const [includeFavorites, setIncludeFavorites] = useState(true);
  const [catalogIds, setCatalogIds] = useState<string[]>([]);

  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs, enabled: open });

  // Reset to the scope step on each (re)open, in render as React documents rather than in an effect.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setPhase({ kind: "scope" });
      setIncludeMovies(true);
      setIncludeEpisodes(true);
      setIncludeFavorites(true);
      setCatalogIds([]);
    }
  }

  const runPreview = async () => {
    setPhase({ kind: "previewing" });
    try {
      const preview = await mediaServer.previewWatchHistorySync(
        providerKey,
        buildScopeRequest(includeMovies, includeEpisodes, catalogIds),
      );
      // Favorites are their own comparison — works, not plays — but they belong to the same explicit
      // sync the user just asked for, so they are previewed alongside rather than behind a second
      // button. A provider that carries no favorites simply contributes nothing here.
      const favorites = includeFavorites && supportsFavorites
        ? await mediaServer.previewFavoritesSync(providerKey).catch(() => null)
        : null;
      setPhase({ kind: "preview", preview, favorites });
    } catch (error) {
      setPhase({ kind: "error", message: errorMessage(error) });
    }
  };

  const runApply = async (runId: string, applyFavorites: boolean) => {
    setPhase({ kind: "applying" });
    try {
      const result = await mediaServer.applyWatchHistorySync(providerKey, runId);
      if (applyFavorites) {
        // Applied after the history run so a favorites failure cannot roll back plays that already
        // landed; its own errors surface through the connection's favorites state.
        await mediaServer.applyFavoritesSync(providerKey).catch(() => null);
      }

      setPhase({ kind: "result", result });
      onApplied();
    } catch (error) {
      setPhase({ kind: "error", message: errorMessage(error) });
    }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Sync with {providerName}</DialogTitle>
          <DialogDescription>
            Compare your library with {providerName}, then apply the changes you approve. Nothing is
            written until you do.
          </DialogDescription>
        </DialogHeader>

        {phase.kind === "scope" && (
          <div className="flex flex-col gap-4 text-sm">
            <div className="flex flex-col gap-2">
              <span className="text-muted-foreground">What to compare</span>
              <div className="flex gap-4">
                <label className="flex items-center gap-2">
                  <Checkbox checked={includeMovies} onCheckedChange={(v) => setIncludeMovies(v === true)} />
                  Movies
                </label>
                <label className="flex items-center gap-2">
                  <Checkbox checked={includeEpisodes} onCheckedChange={(v) => setIncludeEpisodes(v === true)} />
                  Episodes
                </label>
                {supportsFavorites && (
                  <label className="flex items-center gap-2">
                    <Checkbox checked={includeFavorites} onCheckedChange={(v) => setIncludeFavorites(v === true)} />
                    Favorites
                  </label>
                )}
              </div>
            </div>
            {(catalogs.data?.length ?? 0) > 0 && (
              <div className="flex flex-col gap-2">
                <span className="text-muted-foreground">Catalogs (all, unless you pick some)</span>
                <div className="flex flex-col gap-1.5">
                  {catalogs.data!.map((catalog) => (
                    <label key={catalog.id} className="flex items-center gap-2">
                      <Checkbox
                        checked={catalogIds.includes(catalog.id)}
                        onCheckedChange={(v) =>
                          setCatalogIds((ids) =>
                            v === true ? [...ids, catalog.id] : ids.filter((id) => id !== catalog.id),
                          )
                        }
                      />
                      <span className="truncate">{catalog.name}</span>
                    </label>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}

        {phase.kind === "previewing" && (
          <div className="flex items-center gap-2 text-sm text-muted-foreground">
            <Spinner /> Comparing with {providerName}…
          </div>
        )}

        {phase.kind === "preview" && (
          <PreviewBody preview={phase.preview} favorites={phase.favorites} providerName={providerName} />
        )}

        {phase.kind === "applying" && (
          <div className="flex items-center gap-2 text-sm text-muted-foreground">
            <Spinner /> Applying…
          </div>
        )}

        {phase.kind === "result" && <ResultBody result={phase.result} />}

        {phase.kind === "error" && <p className="text-sm text-destructive">{phase.message}</p>}

        <DialogFooter>
          <SyncFooter
            phase={phase}
            providerName={providerName}
            canPreview={canPreview(includeMovies, includeEpisodes)}
            onPreview={runPreview}
            onApply={runApply}
            onClose={() => onOpenChange(false)}
            onBack={() => setPhase({ kind: "scope" })}
          />
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function PreviewBody({
  preview,
  favorites,
  providerName,
}: {
  preview: WatchHistorySyncPreview;
  favorites: FavoritesSyncPlan | null;
  providerName: string;
}) {
  const rows = CLASSIFICATIONS.map((entry) => ({ ...entry, count: preview.counts[entry.key] ?? 0 })).filter(
    (entry) => entry.count > 0,
  );
  const willChange = changeCount(preview);

  return (
    <div className="flex flex-col gap-3 text-sm">
      {rows.length === 0 ? (
        <p className="text-muted-foreground">Nothing to compare in this scope.</p>
      ) : (
        <ul className="flex flex-col gap-1.5">
          {rows.map((entry) => (
            <li key={entry.key} className="flex items-baseline justify-between gap-3">
              <span className="flex flex-col">
                <span className="font-medium">{entry.label(providerName)}</span>
                <span className="text-muted-foreground text-xs">{entry.hint(providerName)}</span>
              </span>
              <span className="tabular-nums font-medium">{entry.count}</span>
            </li>
          ))}
        </ul>
      )}

      {favorites && <FavoritesSummary plan={favorites} providerName={providerName} />}

      {hasBlockingWork(preview) && (
        <Warning>
          Some of your recent changes are still on their way to {providerName}. Sync is paused until
          they arrive so it does not undo them — try again shortly.
        </Warning>
      )}
      {!hasBlockingWork(preview) && preview.aggregateCountsMayCollapse && (
        <Warning>
          Some titles have a play count but no per-play history. Syncing keeps them watched but records
          a single play, so a higher count may drop to one.
        </Warning>
      )}
      {!hasBlockingWork(preview) && willChange === 0 && rows.length > 0 && (
        <p className="text-muted-foreground text-xs">Nothing needs importing or exporting.</p>
      )}
    </div>
  );
}

function ResultBody({ result }: { result: WatchHistorySyncResult }) {
  const skips = (Object.entries(result.skipped) as Array<[WatchHistorySyncSkip, number]>).filter(
    ([, count]) => count > 0,
  );
  return (
    <div className="flex flex-col gap-3 text-sm">
      <ul className="flex flex-col gap-1.5">
        <Stat icon={<ArrowDownToLine className="size-4" />} label="Imported" value={result.imported} />
        <Stat icon={<ArrowUpFromLine className="size-4" />} label="Sent" value={result.exported} />
        <Stat icon={<RefreshCw className="size-4" />} label="Unchanged" value={result.unchanged} />
      </ul>
      {skips.length > 0 && (
        <p className="text-muted-foreground text-xs">
          Skipped:{" "}
          {skips.map(([reason, count], index) => (
            <span key={reason}>
              {index > 0 ? ", " : ""}
              {count} {SKIP_LABELS[reason]}
            </span>
          ))}
          .
        </p>
      )}
    </div>
  );
}

function Stat({ icon, label, value }: { icon: React.ReactNode; label: string; value: number }) {
  return (
    <li className="flex items-center justify-between gap-3">
      <span className="flex items-center gap-2 text-muted-foreground">
        {icon}
        {label}
      </span>
      <span className="tabular-nums font-medium">{value}</span>
    </li>
  );
}

function Warning({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex gap-2 rounded-md border border-amber-500/40 bg-amber-500/10 p-2.5 text-xs">
      <AlertTriangle className="size-4 shrink-0 text-amber-600" />
      <span>{children}</span>
    </div>
  );
}

function SyncFooter({
  phase,
  providerName,
  canPreview,
  onPreview,
  onApply,
  onClose,
  onBack,
}: {
  phase: Phase;
  providerName: string;
  canPreview: boolean;
  onPreview: () => void;
  onApply: (runId: string, applyFavorites: boolean) => void;
  onClose: () => void;
  onBack: () => void;
}) {
  switch (phase.kind) {
    case "scope":
      return (
        <>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button size="sm" disabled={!canPreview} onClick={onPreview}>
            Preview
          </Button>
        </>
      );
    case "previewing":
    case "applying":
      return (
        <Button variant="ghost" size="sm" onClick={onClose}>
          Cancel
        </Button>
      );
    case "preview":
      return (
        <>
          <Button variant="ghost" size="sm" onClick={onBack}>
            Back
          </Button>
          <Button
            size="sm"
            disabled={!canApply(phase.preview)}
            onClick={() => onApply(phase.preview.runId, phase.favorites !== null)}
          >
            {`Apply to ${providerName}`}
          </Button>
        </>
      );
    case "result":
      return (
        <Button size="sm" onClick={onClose}>
          Done
        </Button>
      );
    case "error":
      return (
        <>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Close
          </Button>
          <Button size="sm" onClick={onBack}>
            Start over
          </Button>
        </>
      );
  }
}

// The favorites half of the comparison: works, not plays. Only the actions with something to do are
// listed; "not in your library" is called out separately because nothing will happen to those.
const FAVORITE_ACTIONS: Array<{ key: FavoriteSyncAction; label: (provider: string) => string }> = [
  { key: "AddLocally", label: (p) => `Favorite here (from ${p})` },
  { key: "AddRemotely", label: (p) => `Favorite on ${p}` },
  { key: "RemoveLocally", label: (p) => `Unfavorite here (removed on ${p})` },
  { key: "RemoveRemotely", label: (p) => `Unfavorite on ${p}` },
];

function FavoritesSummary({ plan, providerName }: { plan: FavoritesSyncPlan; providerName: string }) {
  const rows = FAVORITE_ACTIONS.map((entry) => ({ ...entry, count: plan.counts[entry.key] ?? 0 })).filter(
    (entry) => entry.count > 0,
  );
  const skipped = plan.counts.SkippedNotInLibrary ?? 0;
  const full = plan.remoteCount != null && plan.capacity != null && plan.remoteCount >= plan.capacity;

  return (
    <div className="flex flex-col gap-1.5 border-t pt-3">
      <span className="text-muted-foreground text-xs">
        Favorites
        {plan.remoteCount != null && plan.capacity != null && (
          <span className={full ? "text-destructive" : ""}>
            {" "}
            · {plan.remoteCount}/{plan.capacity} on {providerName}
          </span>
        )}
      </span>
      {rows.length === 0 && skipped === 0 ? (
        <p className="text-muted-foreground">Favorites are already in sync.</p>
      ) : (
        <ul className="flex flex-col gap-1">
          {rows.map((entry) => (
            <li key={entry.key} className="flex items-baseline justify-between gap-3">
              <span>{entry.label(providerName)}</span>
              <span className="tabular-nums font-medium">{entry.count}</span>
            </li>
          ))}
        </ul>
      )}
      {skipped > 0 && (
        <span className="text-muted-foreground text-xs">
          {skipped} favorite{skipped === 1 ? "" : "s"} on {providerName} match nothing in this library and
          will be left alone.
        </span>
      )}
      {full && (
        <Warning>
          Your {providerName} favorites are full ({plan.capacity}), so new ones can’t be added there until
          you remove some.
        </Warning>
      )}
    </div>
  );
}
