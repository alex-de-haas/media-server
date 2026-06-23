"use client";

import { useMemo, useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, MoreVertical, Pause, Play, RotateCw, SearchCheck, Square, Trash2 } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type Catalog, type Download, type IngestItem, type IngestSourceFile, type VpnStatus } from "@/lib/media-server";
import { formatEta, formatPercent, formatSpeed, formatTimeAgo } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
import { cn } from "@/lib/utils";
import { useSession } from "@/components/app-shell";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { QueryState } from "@/components/states";
import { IngestStepper, type StepActivity } from "@/components/ingest-stepper";
import { IngestReviewDialog } from "@/components/ingest-review-dialog";
import { AddTorrentDialog } from "@/components/add-torrent-dialog";

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

  const catalogsById = useMemo(
    () => new Map((catalogs.data ?? []).map((catalog) => [catalog.id, catalog])),
    [catalogs.data],
  );
  const downloadsById = useMemo(
    () => new Map((downloads.data ?? []).map((download) => [download.id, download])),
    [downloads.data],
  );

  const downloadFor = (item: IngestItem) => (item.downloadId ? downloadsById.get(item.downloadId) : undefined);

  // "Delete all" on the Done tab: drops every published tracking row at once; library files stay put.
  const clearDone = useMutation({
    mutationFn: mediaServer.deleteDoneIngest,
    onSuccess: (removed) => {
      queryClient.invalidateQueries({ queryKey: ["ingest"] });
      queryClient.invalidateQueries({ queryKey: ["downloads"] });
      toast.success(removed > 0 ? `Removed ${removed} item${removed === 1 ? "" : "s"}` : "Nothing to remove");
    },
    onError: (error) => toast.error("Couldn’t delete completed items", { description: errorMessage(error) }),
  });

  const counts = useMemo(() => {
    const tally: Record<TabKey, number> = { active: 0, done: 0 };
    for (const item of ingest.data ?? []) {
      tally[tabOf(item, item.downloadId ? downloadsById.get(item.downloadId) : undefined)] += 1;
    }
    return tally;
  }, [ingest.data, downloadsById]);

  return (
    <Card>
      <CardHeader>
        <CardTitle>Activity</CardTitle>
        <CardDescription>Torrents from download through the ingest pipeline into your library.</CardDescription>
        <CardAction>
          <div className="flex items-center gap-2">
            <VpnBadge status={vpn.data ?? null} />
            <AddTorrentDialog />
          </div>
        </CardAction>
      </CardHeader>
      <CardContent className="flex flex-col gap-3 text-sm">
        <TabBar
          tab={tab}
          counts={counts}
          onChange={setTab}
          action={
            tab === "done" && role === "admin" && counts.done > 0 ? (
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
            ) : null
          }
        />
        <QueryState query={ingest} empty="Nothing here yet. Add a torrent to get started.">
          {(items) => {
            const visible = items.filter((item) => tabOf(item, downloadFor(item)) === tab);
            if (visible.length === 0) {
              return <p className="text-muted-foreground text-sm">{EMPTY[tab]}</p>;
            }
            return visible.map((item) => (
              <IngestRow
                key={item.id}
                item={item}
                catalog={catalogsById.get(item.catalogId)}
                download={downloadFor(item)}
              />
            ));
          }}
        </QueryState>
      </CardContent>

      <ClearDoneDialog
        open={clearOpen}
        onOpenChange={setClearOpen}
        count={counts.done}
        onConfirm={() => {
          clearDone.mutate();
          setClearOpen(false);
        }}
      />
    </Card>
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

function TabBar({
  tab,
  counts,
  onChange,
  action,
}: {
  tab: TabKey;
  counts: Record<TabKey, number>;
  onChange: (tab: TabKey) => void;
  action?: ReactNode;
}) {
  return (
    <div className="flex items-center justify-between border-b">
      <div role="tablist" className="flex items-center gap-1">
        {TABS.map(({ key, label }) => (
          <button
            key={key}
            type="button"
            role="tab"
            aria-selected={tab === key}
            onClick={() => onChange(key)}
            className={cn(
              "-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors",
              tab === key
                ? "border-brand text-foreground"
                : "border-transparent text-muted-foreground hover:text-foreground",
            )}
          >
            {label}
            {counts[key] > 0 && <span className="text-muted-foreground ml-1.5 text-xs">{counts[key]}</span>}
          </button>
        ))}
      </div>
      {action}
    </div>
  );
}

// The Download step maps to a torrent; reflect its live transfer/pause state on that step so the stepper
// shows a spinner while downloading and a pause glyph when paused. Other stages fall back to the coarse
// ingest `status` inside the stepper itself.
function downloadActivity(item: IngestItem, download: Download | undefined): StepActivity | undefined {
  if (item.stage !== "Download" || !download) return undefined;
  if (isDownloadPaused(download)) return "paused";
  if (!DOWNLOAD_DONE_STATES.includes(download.state)) return "running";
  return undefined;
}

// A human explanation of why an item sits where it does — especially the "Pending with no error" states
// that otherwise look identical and inert. Mirrors the pipeline stage semantics on the server.
function stateHint(item: IngestItem, download: Download | undefined): string | null {
  if (item.status !== "Pending") return null;
  if (item.lastError) return null; // surfaced through the warning icon instead.
  if (item.stage === "Download") {
    if (download && isDownloadPaused(download)) return "Download paused.";
    return "Waiting for the download to finish.";
  }
  if (item.sourceFiles.length === 0) return "Waiting for the torrent's file list — no files received yet.";
  return "Queued, waiting to be processed.";
}

// The amber warning tooltip text for items that need attention, or null when the item is healthy.
function warningText(item: IngestItem): string | null {
  if (item.status === "NeedsReview") return "Needs a metadata match — resolve it to continue.";
  if (item.status === "Failed") return item.lastError ?? "This item failed. Retry to run it again.";
  return null;
}

function IngestRow({ item, catalog, download }: { item: IngestItem; catalog: Catalog | undefined; download: Download | undefined }) {
  const { role } = useSession();
  const queryClient = useQueryClient();
  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ["ingest"] });
    queryClient.invalidateQueries({ queryKey: ["downloads"] });
  };
  const onError = (action: string) => (error: unknown) => toast.error(`Couldn’t ${action}`, { description: errorMessage(error) });

  const retry = useMutation({
    mutationFn: () => mediaServer.retryIngest(item.id),
    onSuccess: () => {
      invalidate();
      toast.success("Retry queued");
    },
    onError: onError("retry"),
  });
  const pause = useMutation({ mutationFn: (id: string) => mediaServer.pauseDownload(id), onSuccess: invalidate, onError: onError("pause download") });
  const resume = useMutation({ mutationFn: (id: string) => mediaServer.resumeDownload(id), onSuccess: invalidate, onError: onError("resume download") });
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

  const category = categoryOf(item, download);
  const title =
    item.mediaTitle ??
    item.downloadName ??
    (item.sourceFiles?.[0]?.relativePath ? displayPath(item.sourceFiles[0].relativePath) : undefined) ??
    "Untitled item";
  const age = formatTimeAgo(item.createdAt);
  const hint = stateHint(item, download);
  const warning = warningText(item);
  const published = item.status === "Done" && item.mediaItemId != null;

  const meta = [catalog?.name, age && `added ${age}`, item.attemptCount > 0 && `attempt ${item.attemptCount}/5`]
    .filter(Boolean)
    .join(" · ");

  const transferring = download !== undefined && !DOWNLOAD_DONE_STATES.includes(download.state);

  return (
    <div className="flex flex-col gap-3 rounded-md border p-3">
      {category === "active" && (
        <IngestStepper
          stage={item.stage}
          stagesCompleted={item.stagesCompleted}
          status={item.status}
          activity={downloadActivity(item, download)}
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
          {meta && <p className="text-muted-foreground text-xs">{meta}</p>}
          {category === "active" && hint && <p className="text-muted-foreground text-xs">{hint}</p>}
          {category === "active" && item.status !== "NeedsReview" && item.sourceFiles.length > 0 && (
            <FileSummary files={item.sourceFiles} />
          )}
        </div>

        <div className="flex shrink-0 items-center gap-1">
          {transferring && download && (
            isDownloadPaused(download) ? (
              <IconAction label="Resume" icon={<Play />} onClick={() => resume.mutate(download.id)} />
            ) : (
              <IconAction label="Pause" icon={<Pause />} onClick={() => pause.mutate(download.id)} />
            )
          )}
          {category === "seeding" && download && (
            <IconAction label="Stop seeding" icon={<Square />} onClick={() => stopSeeding.mutate(download.id)} />
          )}
          {item.status === "NeedsReview" && (
            <IconAction label="Resolve match" icon={<SearchCheck />} onClick={() => setReviewOpen(true)} />
          )}
          {item.status === "Failed" && (
            <IconAction label="Retry" icon={<RotateCw />} onClick={() => retry.mutate()} disabled={retry.isPending} />
          )}
          {role === "admin" && (
            <DropdownMenu>
              <DropdownMenuTrigger
                render={
                  <Button variant="ghost" size="icon-sm" aria-label="More actions">
                    <MoreVertical />
                  </Button>
                }
              />
              <DropdownMenuContent>
                {item.status === "NeedsReview" && (
                  <DropdownMenuItem onClick={() => retry.mutate()} disabled={retry.isPending}>
                    <RotateCw />
                    Retry
                  </DropdownMenuItem>
                )}
                <DropdownMenuItem variant="destructive" onClick={() => setConfirmOpen(true)}>
                  <Trash2 />
                  Remove
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          )}
        </div>
      </div>

      {category === "active" && transferring && download && <DownloadProgress download={download} />}
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

function DownloadProgress({ download }: { download: Download }) {
  const percent = download.percentComplete ?? 0;
  const eta =
    download.downloadRateBytesPerSecond && download.sizeBytes
      ? (download.sizeBytes * (1 - percent / 100)) / download.downloadRateBytesPerSecond
      : null;

  return (
    <div>
      <div className="bg-secondary h-2 w-full overflow-hidden rounded-full">
        <div className="bg-primary h-full transition-[width] duration-500" style={{ width: `${Math.min(percent, 100)}%` }} />
      </div>
      <div className="text-muted-foreground mt-2 flex flex-wrap gap-x-4 gap-y-1 font-mono">
        <span>{formatPercent(download.percentComplete)}</span>
        <span>↓ {formatSpeed(download.downloadRateBytesPerSecond)}</span>
        <span>↑ {formatSpeed(download.uploadRateBytesPerSecond)}</span>
        <span>ETA {formatEta(eta)}</span>
      </div>
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

function FileSummary({ files }: { files: IngestSourceFile[] }) {
  const shown = files.slice(0, 2);
  const rest = files.length - shown.length;
  return (
    <ul className="text-muted-foreground flex flex-col gap-0.5 text-xs">
      {shown.map((file) => (
        <li key={file.id} className="truncate" title={displayPath(file.relativePath)}>
          {displayPath(file.relativePath)}
        </li>
      ))}
      {rest > 0 && (
        <li>
          +{rest} more file{rest > 1 ? "s" : ""}
        </li>
      )}
    </ul>
  );
}

function IconAction({
  label,
  icon,
  onClick,
  disabled,
}: {
  label: string;
  icon: ReactNode;
  onClick: () => void;
  disabled?: boolean;
}) {
  return (
    <Tooltip>
      <TooltipTrigger
        render={
          <Button variant="ghost" size="icon-sm" aria-label={label} onClick={onClick} disabled={disabled}>
            {icon}
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
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Remove item?</DialogTitle>
          <DialogDescription>
            Remove <span className="text-foreground font-medium">{title}</span> from Activity.{" "}
            {published
              ? "The published item stays in your library — delete it from its detail page."
              : "Any in-progress download and staging files are cleaned up."}
          </DialogDescription>
        </DialogHeader>

        <DialogFooter>
          <Button type="button" variant="ghost" size="sm" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button type="button" variant="destructive" size="sm" onClick={onConfirm}>
            Remove
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
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
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Delete all completed items?</DialogTitle>
          <DialogDescription>
            Remove{" "}
            <span className="text-foreground font-medium">
              {count} completed item{count === 1 ? "" : "s"}
            </span>{" "}
            from Activity. Published items stay in your library — this only clears the Done list.
          </DialogDescription>
        </DialogHeader>

        <DialogFooter>
          <Button type="button" variant="ghost" size="sm" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button type="button" variant="destructive" size="sm" onClick={onConfirm}>
            Delete all
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
