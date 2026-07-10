"use client";

import { useMemo, useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, ArrowDown, ArrowLeftRight, ArrowUp, Clock, Loader2, type LucideIcon, Pause, Play, RotateCw, SearchCheck, Square, Trash2, Wand2 } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type Catalog, type Download, type IngestItem, type IngestSourceFile, type LibraryMoveJob, type TranscodeJob, type VpnStatus } from "@/lib/media-server";
import { formatEta, formatPercent, formatSpeed, formatTimeAgo } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
import { cn } from "@/lib/utils";
import { useSession } from "@/components/app-shell";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Progress } from "@/components/ui/progress";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { EmptyState, ErrorState, Loading } from "@/components/states";
import { IngestStepper, type StepActivity } from "@/components/ingest-stepper";
import { IngestReviewDialog } from "@/components/ingest-review-dialog";
import { AddTorrentDialog } from "@/components/add-torrent-dialog";
import { TranscodeJobRow, isTranscodeActive } from "@/components/transcode";

// Download states where the torrent is no longer actively transferring (content is on disk).
const DOWNLOAD_DONE_STATES = ["Completed", "Seeding", "StoppedSeeding"];

type TabKey = "active" | "done";

// The per-item category drives row rendering (stepper vs seeding controls); the Active tab groups the
// first two so seeding items live alongside everything still in flight.
type Category = "active" | "seeding" | "done";

const TABS: { key: TabKey; label: string }[] = [
  { key: "active", label: "Active" },
  { key: "done", label: "Done" },
];

const EMPTY: Record<TabKey, string> = {
  active: "Nothing in the pipeline right now.",
  done: "No completed items yet.",
};

function isDownloadPaused(download: Download): boolean {
  return /paus/i.test(download.engineState ?? download.state);
}

// A torrent kept seeding parks its ingest at the download stage (still seedable — shown with a Seeding
// badge and a Stop seeding action); everything else still in flight is active; published is done.
function categoryOf(item: IngestItem, download: Download | undefined): Category {
  if (download?.state === "Seeding") return "seeding";
  if (item.status !== "Done") return "active";
  return "done";
}

// Which tab an item shows under: Active groups in-flight + seeding; Done is published.
function tabOf(item: IngestItem, download: Download | undefined): TabKey {
  return categoryOf(item, download) === "done" ? "done" : "active";
}

export function ActivitySection() {
  const { role } = useSession();
  const queryClient = useQueryClient();
  const [tab, setTab] = useState<TabKey>("active");
  const [clearOpen, setClearOpen] = useState(false);
  // Realtime is pushed over SSE (see RealtimeBridge); these slow intervals are only a reconnect fallback.
  const ingest = useQuery({ queryKey: ["ingest"], queryFn: mediaServer.listIngest, refetchInterval: 20000 });
  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs });
  // Live torrent progress/state is patched into this cache by SSE; the interval is a fallback only.
  const downloads = useQuery({ queryKey: ["downloads"], queryFn: mediaServer.listDownloads, refetchInterval: 20000 });
  // Engine-wide VPN tunnel status (null when downloading is in-process). Pushed over SSE; slow interval is a fallback.
  const vpn = useQuery({ queryKey: ["vpn"], queryFn: mediaServer.getVpnStatus, refetchInterval: 30000 });
  // In-flight cross-catalog moves. Seeded here, then kept live by RealtimeBridge over SSE (progress per byte).
  // Only admins can move (and read the admin-only active list), so don't fire the request for other roles.
  const moves = useQuery({
    queryKey: ["library-move-jobs"],
    queryFn: mediaServer.listActiveMoves,
    enabled: role === "admin",
  });
  // Conversions (re-encodes). Admin-only like the /api/transcode surface; not pushed over SSE, so poll while
  // any job is still running (idle otherwise). Active ones group under the Active tab, finished ones under Done.
  const transcodes = useQuery({
    queryKey: ["transcode-jobs"],
    queryFn: mediaServer.listTranscodeJobs,
    enabled: role === "admin",
    refetchInterval: (query) => ((query.state.data as TranscodeJob[] | undefined)?.some(isTranscodeActive) ? 2000 : false),
  });
  // Only "down" when there's a VPN to report and it's disconnected — the engine pauses transfers behind the
  // killswitch, so the cards show "Paused — VPN down" rather than a stalled 0 B/s.
  const vpnDown = vpn.data != null && !vpn.data.connected;

  const catalogsById = useMemo(
    () => new Map((catalogs.data ?? []).map((catalog) => [catalog.id, catalog])),
    [catalogs.data],
  );
  const downloadsById = useMemo(
    () => new Map((downloads.data ?? []).map((download) => [download.id, download])),
    [downloads.data],
  );

  const downloadFor = (item: IngestItem) => (item.downloadId ? downloadsById.get(item.downloadId) : undefined);

  // Ingest tallies — the done count feeds the "Delete all" gate/dialog alongside finished conversions.
  const counts = useMemo(() => {
    const tally: Record<TabKey, number> = { active: 0, done: 0 };
    for (const item of ingest.data ?? []) {
      tally[tabOf(item, item.downloadId ? downloadsById.get(item.downloadId) : undefined)] += 1;
    }
    return tally;
  }, [ingest.data, downloadsById]);

  const moveList = moves.data ?? [];
  const activeTranscodes = useMemo(() => (transcodes.data ?? []).filter(isTranscodeActive), [transcodes.data]);
  const doneTranscodes = useMemo(() => (transcodes.data ?? []).filter((job) => !isTranscodeActive(job)), [transcodes.data]);

  // Everything the Done tab shows: published tracking rows (ingest) plus finished conversions.
  const doneCount = counts.done + doneTranscodes.length;

  // "Delete all" on the Done tab clears both of those groups in one action — dropping only the ingest rows
  // (as it used to) left every finished conversion card behind. The ingest rows go in a single bulk call;
  // each finished conversion is dismissed like its per-card trash button (the encoded file already lives in
  // the library and is left untouched). Library files always stay put.
  const clearDone = useMutation({
    mutationFn: async () => {
      const conversions = doneTranscodes;
      const [removedIngest] = await Promise.all([
        counts.done > 0 ? mediaServer.deleteDoneIngest() : Promise.resolve(0),
        Promise.all(conversions.map((job) => mediaServer.removeTranscodeJob(job.id))),
      ]);
      return removedIngest + conversions.length;
    },
    onSuccess: (removed) => {
      queryClient.invalidateQueries({ queryKey: ["ingest"] });
      queryClient.invalidateQueries({ queryKey: ["downloads"] });
      queryClient.invalidateQueries({ queryKey: ["transcode-jobs"] });
      toast.success(removed > 0 ? `Removed ${removed} item${removed === 1 ? "" : "s"}` : "Nothing to remove");
    },
    onError: (error) => toast.error("Couldn’t delete completed items", { description: errorMessage(error) }),
  });

  // Tab badges cover every category the tab shows, not just ingest: Active also holds moves + running
  // conversions; Done also holds finished conversions.
  const badgeCounts: Record<TabKey, number> = {
    active: counts.active + moveList.length + activeTranscodes.length,
    done: doneCount,
  };

  // The published-library history (ingest) filtered to one tab, with the download group header when other
  // categories share the tab. Loading/error/empty handled here so the extra groups can render alongside.
  const renderIngest = (key: TabKey, hasOtherGroups: boolean): ReactNode => {
    if (ingest.isPending) return hasOtherGroups ? null : <Loading />;
    if (ingest.isError) return <ErrorState onRetry={() => void ingest.refetch()} />;
    const all = ingest.data ?? [];
    if (all.length === 0) {
      return hasOtherGroups ? null : <EmptyState>Nothing here yet. Add a torrent to get started.</EmptyState>;
    }
    const visible = all.filter((item) => tabOf(item, downloadFor(item)) === key);
    if (visible.length === 0) {
      return hasOtherGroups ? null : <p className="text-muted-foreground text-sm">{EMPTY[key]}</p>;
    }
    const rows = visible.map((item) => (
      <IngestRow key={item.id} item={item} catalog={catalogsById.get(item.catalogId)} download={downloadFor(item)} vpnDown={vpnDown} />
    ));
    // A lone downloads list keeps today's headerless look; a header appears only to separate it from siblings.
    return key === "active" && hasOtherGroups ? (
      <ActivityGroup icon={ArrowDown} label="Downloads">
        {rows}
      </ActivityGroup>
    ) : (
      <div className="flex flex-col gap-3">{rows}</div>
    );
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>Activity</CardTitle>
        <CardDescription>Downloads, catalog moves, and conversions in your library.</CardDescription>
        <CardAction>
          <div className="flex flex-wrap items-center justify-end gap-2">
            <ThroughputBadge downloads={downloads.data ?? []} vpnDown={vpnDown} />
            <VpnBadge status={vpn.data ?? null} />
            <AddTorrentDialog />
          </div>
        </CardAction>
      </CardHeader>
      <CardContent className="text-sm">
        <Tabs value={tab} onValueChange={(value) => setTab(value as TabKey)}>
          <div className="flex items-center justify-between border-b">
            <TabsList variant="line">
              {TABS.map(({ key, label }) => (
                <TabsTrigger key={key} value={key}>
                  {label}
                  {badgeCounts[key] > 0 && <span className="text-muted-foreground text-xs">{badgeCounts[key]}</span>}
                </TabsTrigger>
              ))}
            </TabsList>
            {tab === "done" && role === "admin" && doneCount > 0 && (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                className="text-muted-foreground hover:text-destructive"
                onClick={() => setClearOpen(true)}
              >
                <Trash2 />
                Delete all
              </Button>
            )}
          </div>

          <TabsContent value="active" className="flex flex-col gap-4 pt-3">
            {moveList.length > 0 && (
              <ActivityGroup icon={ArrowLeftRight} label="Moving to another catalog">
                {/* The one that's copying first, queued ones below it. */}
                {moveList
                  .slice()
                  .sort((a, b) => Number(a.queued ?? false) - Number(b.queued ?? false))
                  .map((move) => (
                    <MoveProgressRow key={move.jobId} move={move} />
                  ))}
              </ActivityGroup>
            )}
            {activeTranscodes.length > 0 && (
              <ActivityGroup icon={Wand2} label="Converting">
                {activeTranscodes.map((job) => (
                  <TranscodeJobRow key={job.id} job={job} />
                ))}
              </ActivityGroup>
            )}
            {renderIngest("active", moveList.length > 0 || activeTranscodes.length > 0)}
          </TabsContent>

          <TabsContent value="done" className="flex flex-col gap-4 pt-3">
            {doneTranscodes.length > 0 && (
              <ActivityGroup icon={Wand2} label="Conversions">
                {doneTranscodes.map((job) => (
                  <TranscodeJobRow key={job.id} job={job} />
                ))}
              </ActivityGroup>
            )}
            {renderIngest("done", doneTranscodes.length > 0)}
          </TabsContent>
        </Tabs>
      </CardContent>

      <ClearDoneDialog
        open={clearOpen}
        onOpenChange={setClearOpen}
        count={doneCount}
        onConfirm={() => {
          clearDone.mutate();
          setClearOpen(false);
        }}
      />
    </Card>
  );
}

// Aggregate transfer rate across every torrent, shown in the Activity header: ↓ is the combined download
// speed, ↑ the combined upload (active downloads + anything still seeding). Hidden when idle, and while the
// VPN is down (the engine pauses transfers behind the killswitch, so the rates would just read 0).
function ThroughputBadge({ downloads, vpnDown }: { downloads: Download[]; vpnDown: boolean }) {
  if (vpnDown) {
    return null;
  }

  const downloadRate = downloads.reduce((total, download) => total + (download.downloadRateBytesPerSecond ?? 0), 0);
  const uploadRate = downloads.reduce((total, download) => total + (download.uploadRateBytesPerSecond ?? 0), 0);
  if (downloadRate <= 0 && uploadRate <= 0) {
    return null;
  }

  return (
    <Tooltip>
      <TooltipTrigger
        render={
          <Badge variant="secondary" tabIndex={0} className="gap-2 font-mono tabular-nums">
            <span className="inline-flex items-center gap-1">
              <ArrowDown className="size-3" aria-hidden />
              {formatSpeed(downloadRate)}
            </span>
            <span className="inline-flex items-center gap-1">
              <ArrowUp className="size-3" aria-hidden />
              {formatSpeed(uploadRate)}
            </span>
          </Badge>
        }
      />
      <TooltipContent>Total throughput — ↓ downloading, ↑ uploading / seeding.</TooltipContent>
    </Tooltip>
  );
}

// Engine-wide VPN indicator shown in the Activity header. The tunnel is shared by every download, so it
// belongs here rather than on each card. Hidden entirely when there's no VPN to report (in-process engine).
function VpnBadge({ status }: { status: VpnStatus | null }) {
  if (!status) return null;

  const connected = status.connected;
  const detail = connected ? (status.exitCountry ?? status.exitIp ?? null) : null;

  return (
    <Tooltip>
      <TooltipTrigger
        render={
          <span
            tabIndex={0}
            aria-label={vpnTooltip(status)}
            className={cn(
              "inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5 text-xs font-medium outline-none focus-visible:ring-2 focus-visible:ring-ring",
              connected
                ? "border-emerald-500/30 text-emerald-600 dark:text-emerald-400"
                : "border-destructive/30 text-destructive",
            )}
          >
            <span className={cn("size-1.5 rounded-full", connected ? "bg-emerald-500" : "bg-destructive")} />
            VPN{connected ? (detail ? ` · ${detail}` : "") : " off"}
          </span>
        }
      />
      <TooltipContent>{vpnTooltip(status)}</TooltipContent>
    </Tooltip>
  );
}

function vpnTooltip(status: VpnStatus): string {
  if (!status.connected) {
    return "VPN tunnel is down — torrent traffic is blocked by the killswitch.";
  }
  // exitIp and exitCountry can be independently null, so surface whichever is known.
  const exitValue = [status.exitIp, status.exitCountry].filter(Boolean).join(" · ");
  const parts = [
    exitValue ? `exit ${exitValue}` : null,
    status.tunnelAddress ? `tunnel ${status.tunnelAddress}` : null,
  ].filter(Boolean);
  return parts.length > 0 ? `Traffic egresses through the VPN — ${parts.join(", ")}.` : "VPN tunnel is up.";
}

// The Download step maps to a torrent; reflect its live transfer/pause state on that step so the stepper
// shows a spinner while downloading and a pause glyph when paused. Other stages fall back to the coarse
// ingest `status` inside the stepper itself.
function downloadActivity(item: IngestItem, download: Download | undefined, vpnDown: boolean): StepActivity | undefined {
  if (item.stage !== "Download" || !download) return undefined;
  if (vpnDown || isDownloadPaused(download)) return "paused";
  if (!DOWNLOAD_DONE_STATES.includes(download.state)) return "running";
  return undefined;
}

// A human explanation of why an item sits where it does — especially the "Pending with no error" states
// that otherwise look identical and inert. Mirrors the pipeline stage semantics on the server.
function stateHint(item: IngestItem): string | null {
  if (item.status !== "Pending") return null;
  if (item.lastError) return null; // surfaced through the warning icon instead.
  // The progress bar and the stepper's pause glyph already convey the Download stage; no text needed.
  if (item.stage === "Download") return null;
  if (item.sourceFiles.length === 0) return "Waiting for the torrent's file list — no files received yet.";
  return "Queued, waiting to be processed.";
}

// The amber warning tooltip text for items that need attention, or null when the item is healthy.
function warningText(item: IngestItem): string | null {
  if (item.status === "NeedsReview") return "Needs a metadata match — resolve it to continue.";
  if (item.status === "Failed") return item.lastError ?? "This item failed. Retry to run it again.";
  return null;
}

function IngestRow({
  item,
  catalog,
  download,
  vpnDown,
}: {
  item: IngestItem;
  catalog: Catalog | undefined;
  download: Download | undefined;
  vpnDown: boolean;
}) {
  const { role } = useSession();
  const queryClient = useQueryClient();
  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ["ingest"] });
    queryClient.invalidateQueries({ queryKey: ["downloads"] });
  };
  const onError = (action: string) => (error: unknown) => toast.error(`Couldn’t ${action}`, { description: errorMessage(error) });

  // Pause/resume return before the engine actually flips the download state, so the mutation's own
  // isPending clears too early — the button re-enables before anything visibly changes and an impatient
  // second click double-fires. Track the intended state and keep the control pending (spinner, disabled)
  // until the polled download state reflects it (or the request errors).
  const [transferIntent, setTransferIntent] = useState<"pause" | "resume" | null>(null);

  const retry = useMutation({
    mutationFn: () => mediaServer.retryIngest(item.id),
    onSuccess: () => {
      invalidate();
      toast.success("Retry queued");
    },
    onError: onError("retry"),
  });
  const pause = useMutation({
    mutationFn: (id: string) => mediaServer.pauseDownload(id),
    onSuccess: invalidate,
    onError: (error) => {
      setTransferIntent(null);
      onError("pause download")(error);
    },
  });
  const resume = useMutation({
    mutationFn: (id: string) => mediaServer.resumeDownload(id),
    onSuccess: invalidate,
    onError: (error) => {
      setTransferIntent(null);
      onError("resume download")(error);
    },
  });
  const stopSeeding = useMutation({
    mutationFn: (id: string) => mediaServer.stopSeeding(id),
    onSuccess: () => {
      invalidate();
      toast.success("Stopped seeding");
    },
    onError: onError("stop seeding"),
  });
  // One backend action: deleting the ingest cancels any in-flight download and clears its .incoming/
  // staging (engine resume cache included); a published item's library file is left untouched.
  const remove = useMutation({
    mutationFn: () => mediaServer.deleteIngest(item.id),
    onSuccess: () => {
      invalidate();
      toast.success("Removed");
    },
    onError: onError("remove item"),
  });

  const [reviewOpen, setReviewOpen] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);

  // Derived (no state write needed): pause/resume return before the engine flips the state, so show the
  // control as pending from the click until the polled download catches up to the intent. transferIntent is
  // cleared by the next click or on error; a satisfied intent simply stops being "pending" here.
  const transferPending =
    transferIntent !== null && download
      ? transferIntent === "pause"
        ? !isDownloadPaused(download)
        : isDownloadPaused(download)
      : false;

  const togglePause = () => {
    if (!download) return;
    if (isDownloadPaused(download)) {
      setTransferIntent("resume");
      resume.mutate(download.id);
    } else {
      setTransferIntent("pause");
      pause.mutate(download.id);
    }
  };

  const category = categoryOf(item, download);
  const title = ingestTitle(item);
  const age = formatTimeAgo(item.createdAt);
  const hint = stateHint(item);
  // The stepper's pause glyph already shows the transfer is stopped; the tunnel being down is the
  // non-obvious, actionable part, so surface it through the amber warning icon like a Failed/NeedsReview
  // item rather than as a body line. (Guarded to Pending so a Failed/NeedsReview warning still wins.)
  const vpnPaused = vpnDown && item.stage === "Download" && item.status === "Pending" && !item.lastError;
  const warning = warningText(item) ?? (vpnPaused ? "VPN is down — transfer paused." : null);
  const published = item.status === "Done" && item.mediaItemId != null;

  const meta = [catalog?.name, age && `added ${age}`, item.attemptCount > 0 && `attempt ${item.attemptCount}/5`]
    .filter(Boolean)
    .join(" · ");
  const showFiles = category === "active" && item.status !== "NeedsReview" && item.sourceFiles.length > 0;

  const transferring = download !== undefined && !DOWNLOAD_DONE_STATES.includes(download.state);

  return (
    <div className="flex flex-col gap-3 rounded-md border p-3">
      {category === "active" && (
        <IngestStepper
          stage={item.stage}
          stagesCompleted={item.stagesCompleted}
          status={item.status}
          activity={downloadActivity(item, download, vpnDown)}
        />
      )}

      <div className="flex items-start justify-between gap-2">
        <div className="flex min-w-0 flex-col gap-1">
          <div className="flex min-w-0 items-center gap-1.5">
            <p className="truncate font-medium" title={title}>
              {title}
            </p>
            {category === "seeding" && <Badge variant="secondary">Seeding</Badge>}
            {warning && (
              <Tooltip>
                <TooltipTrigger
                  render={
                    <button
                      type="button"
                      aria-label="Details"
                      className="text-amber-500 shrink-0 rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
                    >
                      <AlertTriangle className="size-4" />
                    </button>
                  }
                />
                <TooltipContent>{warning}</TooltipContent>
              </Tooltip>
            )}
          </div>
          {(meta || showFiles) && (
            <p className="text-muted-foreground text-xs">
              {meta}
              {showFiles && (
                <>
                  {meta && " · "}
                  <FilesTooltip files={item.sourceFiles} />
                </>
              )}
            </p>
          )}
          {category === "active" && hint && <p className="text-muted-foreground text-xs">{hint}</p>}
        </div>

        {/* One icon row, right of the title: only the actions that apply to the item's current step/state. */}
        <div className="flex shrink-0 items-center gap-1">
          {/* No manual pause/resume while the VPN is down — the engine gates transfers and a resume can't
              succeed under the killswitch. */}
          {transferring && download && !vpnDown && (
            transferPending ? (
              <IconAction label={transferIntent === "pause" ? "Pausing…" : "Resuming…"} icon={<Pause />} pending onClick={() => {}} />
            ) : isDownloadPaused(download) ? (
              <IconAction label="Resume" icon={<Play />} onClick={togglePause} />
            ) : (
              <IconAction label="Pause" icon={<Pause />} onClick={togglePause} />
            )
          )}
          {category === "seeding" && download && (
            <IconAction label="Stop seeding" icon={<Square />} pending={stopSeeding.isPending} onClick={() => stopSeeding.mutate(download.id)} />
          )}
          {item.status === "NeedsReview" && (
            <IconAction label="Resolve match" icon={<SearchCheck />} onClick={() => setReviewOpen(true)} />
          )}
          {(item.status === "Failed" || (item.status === "NeedsReview" && role === "admin")) && (
            <IconAction label="Retry" icon={<RotateCw />} pending={retry.isPending} onClick={() => retry.mutate()} />
          )}
          {role === "admin" && (
            <IconAction label="Remove" icon={<Trash2 />} destructive pending={remove.isPending} onClick={() => setConfirmOpen(true)} />
          )}
        </div>
      </div>

      {category === "active" && transferring && download && <DownloadProgress download={download} vpnDown={vpnDown} />}
      {category === "seeding" && download && <SeedingStats download={download} />}

      {item.status === "NeedsReview" && (
        <IngestReviewDialog
          item={item}
          catalog={catalog}
          open={reviewOpen}
          onOpenChange={setReviewOpen}
          onMatched={() => {
            setReviewOpen(false);
            invalidate();
          }}
        />
      )}

      <RemoveDialog
        open={confirmOpen}
        onOpenChange={setConfirmOpen}
        title={title}
        published={published}
        onConfirm={() => {
          remove.mutate();
          setConfirmOpen(false);
        }}
      />
    </div>
  );
}

// A labelled category inside a tab (moving, converting, downloads): a small icon + label header over a stack
// of that category's cards. The header carries the icon, so the cards themselves don't repeat it.
function ActivityGroup({ icon: Icon, label, children }: { icon: LucideIcon; label: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-2">
      <div className="text-muted-foreground flex items-center gap-1.5 text-xs font-medium">
        <Icon className="size-3.5" aria-hidden />
        {label}
      </div>
      <div className="flex flex-col gap-3">{children}</div>
    </section>
  );
}

// A cross-catalog move in flight: file bytes are being relocated (a copy across volumes, an instant rename
// within one). Full progress bar with the live speed/ETA below (per-byte, pushed over SSE), mirroring a
// download card. Labels can be absent for a move stranded by a restart; speed/ETA until the first tick.
function MoveProgressRow({ move }: { move: LibraryMoveJob }) {
  return (
    <div className="flex flex-col gap-2 rounded-md border p-3">
      <p className="min-w-0 truncate font-medium">
        Moving {move.title ? <span title={move.title}>“{move.title}”</span> : "item"}
        {move.targetCatalogName && <span className="text-muted-foreground font-normal"> → {move.targetCatalogName}</span>}
      </p>
      {move.queued ? (
        // Waiting behind the move that's copying now — no bar/stats yet, just say it's queued.
        <span className="text-muted-foreground flex items-center gap-1.5 text-xs">
          <Clock className="size-3.5 shrink-0" aria-hidden />
          Queued
        </span>
      ) : (
        <>
          <Progress value={move.progress} />
          {/* Speed/ETA are each present only mid-copy — the 100% tick has a rate but no ETA — so gate them apart. */}
          <div className="text-muted-foreground flex flex-wrap gap-x-4 gap-y-1 font-mono text-xs tabular-nums">
            <span>{move.progress}%</span>
            {move.bytesPerSecond != null && <span>{formatSpeed(move.bytesPerSecond)}</span>}
            {move.etaSeconds != null && <span>ETA {formatEta(move.etaSeconds)}</span>}
          </div>
        </>
      )}
    </div>
  );
}

function DownloadProgress({ download, vpnDown }: { download: Download; vpnDown: boolean }) {
  const percent = download.percentComplete ?? 0;
  // Prefer the engine's own ETA (derived from real piece completion); fall back to a local estimate for
  // older engine builds that don't report one.
  const eta =
    download.etaSeconds ??
    (download.downloadRateBytesPerSecond && download.sizeBytes
      ? (download.sizeBytes * (1 - percent / 100)) / download.downloadRateBytesPerSecond
      : null);

  const active = !vpnDown && !isDownloadPaused(download);

  return (
    <div className="flex flex-col gap-2">
      <Progress value={Math.min(percent, 100)} />
      <div className="text-muted-foreground flex flex-wrap gap-x-4 gap-y-1 font-mono">
        <span>{formatPercent(download.percentComplete)}</span>
        {vpnDown ? (
          // Transfer is gated by the killswitch — show why instead of a misleading 0 B/s.
          <span className="text-amber-500">Paused · VPN down</span>
        ) : isDownloadPaused(download) ? (
          // Rates/ETA are meaningless (and stale) while paused — say "Paused" instead of misleading numbers.
          <span>Paused</span>
        ) : (
          <>
            <span>↓ {formatSpeed(download.downloadRateBytesPerSecond)}</span>
            <span>↑ {formatSpeed(download.uploadRateBytesPerSecond)}</span>
            <span>ETA {formatEta(eta)}</span>
          </>
        )}
      </div>
      {active && <PeerStats download={download} />}
    </div>
  );
}

// Peer/piece breakdown shown during an active download. Many `availablePeers` (known from
// trackers/DHT/PEX but not connected) while `peers` (connected) stays low is the tell-tale of a
// connectivity/port-forwarding problem behind the VPN rather than a peer-discovery one.
function PeerStats({ download }: { download: Download }) {
  const parts: string[] = [`${download.peers ?? 0} peers`];
  if (download.seeds != null || download.leeches != null) {
    parts.push(`${download.seeds ?? 0} seeds · ${download.leeches ?? 0} leeches`);
  }
  if (download.availablePeers != null && download.availablePeers > 0) {
    parts.push(`${download.availablePeers} known`);
  }
  if (download.totalPieces != null && download.totalPieces > 0) {
    parts.push(`${download.completePieces ?? 0}/${download.totalPieces} pieces`);
  }

  return (
    <div className="text-muted-foreground/80 flex flex-wrap gap-x-4 gap-y-1 font-mono text-xs">
      {parts.map((part, index) => (
        <span key={index}>{part}</span>
      ))}
    </div>
  );
}

function SeedingStats({ download }: { download: Download }) {
  return (
    <div className="text-muted-foreground flex flex-wrap gap-x-4 gap-y-1 font-mono">
      <span>↑ {formatSpeed(download.uploadRateBytesPerSecond)}</span>
      <span>ratio {download.ratio?.toFixed(2) ?? "—"}</span>
      <span>{download.peers ?? 0} peers</span>
    </div>
  );
}

// Source-file paths are catalog-root-relative; while a torrent is still in flight they sit under the
// transient `.incoming/<downloadId>/` staging folder, which is noise to the operator — hide it.
function displayPath(relativePath: string): string {
  return relativePath.replace(/^\.incoming\/[0-9a-f]{32}\//i, "");
}

// The card title is the parsed title the pipeline identifies by — release groups and per-file noise
// (SxxEyy, codecs, the raw filename) stripped — so it reads the same whether or not the item has been
// identified yet. Prefer the resolved media title, then the backend's parsed title, and only fall back to
// the torrent/download name when no files have been parsed yet (e.g. a magnet still fetching metadata).
function ingestTitle(item: IngestItem): string {
  if (item.mediaTitle) return item.mediaTitle;
  const parsed = item.sourceFiles.find((file) => file.parsedTitle?.trim())?.parsedTitle?.trim();
  if (parsed) return parsed;
  if (item.downloadName) return item.downloadName;
  const first = item.sourceFiles[0]?.relativePath;
  return first ? displayPath(first) : "Untitled item";
}

// The torrent's files are detail-on-demand: a compact count on the card, the full list (capped) in a
// tooltip, so a multi-file pack doesn't push three lines of paths onto every card.
function FilesTooltip({ files }: { files: IngestSourceFile[] }) {
  const max = 12;
  const shown = files.slice(0, max);
  const rest = files.length - shown.length;
  return (
    <Tooltip>
      <TooltipTrigger
        render={
          <button
            type="button"
            className="text-muted-foreground focus-visible:ring-ring w-fit rounded-sm text-xs underline decoration-dotted underline-offset-2 outline-none focus-visible:ring-2"
          >
            {files.length} file{files.length > 1 ? "s" : ""}
          </button>
        }
      />
      <TooltipContent className="max-w-md">
        <ul className="flex flex-col gap-0.5 font-mono text-xs">
          {shown.map((file) => (
            <li key={file.id} className="break-all">
              {displayPath(file.relativePath)}
            </li>
          ))}
          {rest > 0 && (
            <li className="opacity-70">
              +{rest} more file{rest > 1 ? "s" : ""}
            </li>
          )}
        </ul>
      </TooltipContent>
    </Tooltip>
  );
}

function IconAction({
  label,
  icon,
  onClick,
  disabled,
  pending,
  destructive,
}: {
  label: string;
  icon: ReactNode;
  onClick: () => void;
  disabled?: boolean;
  // Swaps the icon for a spinner and disables the button — feedback for commands that take a moment to
  // land (and a guard against impatient double-clicks).
  pending?: boolean;
  destructive?: boolean;
}) {
  return (
    <Tooltip>
      <TooltipTrigger
        render={
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label={label}
            aria-busy={pending}
            onClick={onClick}
            disabled={disabled || pending}
            className={cn(destructive && "text-destructive hover:text-destructive hover:bg-destructive/10")}
          >
            {pending ? <Loader2 className="animate-spin" /> : icon}
          </Button>
        }
      />
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}

function RemoveDialog({
  open,
  onOpenChange,
  title,
  published,
  onConfirm,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  published: boolean;
  onConfirm: () => void;
}) {
  return (
    <AlertDialog open={open} onOpenChange={onOpenChange}>
      <AlertDialogContent className="sm:max-w-md">
        <AlertDialogHeader>
          <AlertDialogTitle>Remove item?</AlertDialogTitle>
          <AlertDialogDescription>
            Remove <span className="text-foreground font-medium">{title}</span> from Activity.{" "}
            {published
              ? "The published item stays in your library — delete it from its detail page."
              : "Any in-progress download and staging files are cleaned up."}
          </AlertDialogDescription>
        </AlertDialogHeader>

        <AlertDialogFooter>
          <AlertDialogCancel size="sm">Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" size="sm" onClick={onConfirm}>
            Remove
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

function ClearDoneDialog({
  open,
  onOpenChange,
  count,
  onConfirm,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  count: number;
  onConfirm: () => void;
}) {
  return (
    <AlertDialog open={open} onOpenChange={onOpenChange}>
      <AlertDialogContent className="sm:max-w-md">
        <AlertDialogHeader>
          <AlertDialogTitle>Delete all completed items?</AlertDialogTitle>
          <AlertDialogDescription>
            Remove{" "}
            <span className="text-foreground font-medium">
              {count} completed item{count === 1 ? "" : "s"}
            </span>{" "}
            from Activity, including finished conversions. Published items and converted versions stay in your
            library — this only clears the Done list.
          </AlertDialogDescription>
        </AlertDialogHeader>

        <AlertDialogFooter>
          <AlertDialogCancel size="sm">Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" size="sm" onClick={onConfirm}>
            Delete all
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
