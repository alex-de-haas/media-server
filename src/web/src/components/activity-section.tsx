"use client";

import { useMemo, useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, MoreVertical, RotateCw, SearchCheck, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { mediaServer, type Catalog, type Download, type IngestItem, type IngestSourceFile } from "@/lib/media-server";
import { formatTimeAgo } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
import { useSession } from "@/components/app-shell";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
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

export function ActivitySection() {
  const ingest = useQuery({ queryKey: ["ingest"], queryFn: mediaServer.listIngest, refetchInterval: 3000 });
  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs });
  // Shares the downloads cache with the Downloads page; lets the Download step show live transfer/pause state.
  const downloads = useQuery({ queryKey: ["downloads"], queryFn: mediaServer.listDownloads, refetchInterval: 3000 });
  const catalogsById = useMemo(
    () => new Map((catalogs.data ?? []).map((catalog) => [catalog.id, catalog])),
    [catalogs.data],
  );
  const downloadsById = useMemo(
    () => new Map((downloads.data ?? []).map((download) => [download.id, download])),
    [downloads.data],
  );

  return (
    <Card>
      <CardHeader>
        <CardTitle>Pipeline activity</CardTitle>
        <CardDescription>Each torrent runs through the ingest pipeline. Progress shows on every item.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-2 text-sm">
        <QueryState query={ingest} empty="Nothing in the pipeline.">
          {(items) =>
            items.map((item) => (
              <IngestRow
                key={item.id}
                item={item}
                catalog={catalogsById.get(item.catalogId)}
                download={item.downloadId ? downloadsById.get(item.downloadId) : undefined}
              />
            ))
          }
        </QueryState>
      </CardContent>
    </Card>
  );
}

// Download states where the torrent is no longer actively transferring (content is on disk).
const DOWNLOAD_DONE_STATES = ["Completed", "Seeding", "StoppedSeeding"];

function isDownloadPaused(download: Download): boolean {
  return /paus/i.test(download.engineState ?? download.state);
}

// The Download step maps to a torrent; reflect its live transfer/pause state on that step so the
// stepper shows a spinner while downloading and a pause glyph when the torrent is paused. Other
// stages fall back to the coarse ingest `status` (Running → spinner) inside the stepper itself.
function downloadActivity(item: IngestItem, download: Download | undefined): StepActivity | undefined {
  if (item.stage !== "Download" || !download) return undefined;
  if (isDownloadPaused(download)) return "paused";
  if (!DOWNLOAD_DONE_STATES.includes(download.state)) return "running";
  return undefined;
}

// A human explanation of why an item sits where it does — especially the "Pending with no error"
// states that otherwise look identical and inert. Mirrors the pipeline stage semantics on the server.
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
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["ingest"] });
  const retry = useMutation({
    mutationFn: () => mediaServer.retryIngest(item.id),
    onSuccess: () => {
      invalidate();
      toast.success("Retry queued");
    },
    onError: (error) => toast.error("Couldn’t retry", { description: errorMessage(error) }),
  });
  const remove = useMutation({
    mutationFn: () => mediaServer.deleteIngest(item.id),
    onSuccess: () => {
      invalidate();
      toast.success("Removed from pipeline");
    },
    onError: (error) => toast.error("Couldn’t remove item", { description: errorMessage(error) }),
  });

  const [reviewOpen, setReviewOpen] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);

  const title = item.downloadName ?? item.sourceFiles[0]?.relativePath ?? "Untitled item";
  const age = formatTimeAgo(item.createdAt);
  const hint = stateHint(item, download);
  const warning = warningText(item);
  const activity = downloadActivity(item, download);

  const meta = [catalog?.name, age && `added ${age}`, item.attemptCount > 0 && `attempt ${item.attemptCount}/5`]
    .filter(Boolean)
    .join(" · ");

  const retryable = item.status === "Failed" || item.status === "NeedsReview";

  return (
    <div className="flex flex-col gap-3 rounded-md border p-3">
      <IngestStepper stage={item.stage} stagesCompleted={item.stagesCompleted} status={item.status} activity={activity} />

      <div className="flex items-start justify-between gap-2">
        <div className="flex min-w-0 flex-col gap-1">
          <div className="flex min-w-0 items-center gap-1.5">
            <p className="truncate font-medium" title={title}>
              {title}
            </p>
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
          {hint && <p className="text-muted-foreground text-xs">{hint}</p>}
          {item.status !== "NeedsReview" && item.sourceFiles.length > 0 && <FileSummary files={item.sourceFiles} />}
        </div>

        <div className="flex shrink-0 items-center gap-1">
          {item.status === "NeedsReview" && (
            <IconAction label="Resolve match" icon={<SearchCheck />} onClick={() => setReviewOpen(true)} />
          )}
          {item.status === "Failed" && (
            <IconAction label="Retry" icon={<RotateCw />} onClick={() => retry.mutate()} disabled={retry.isPending} />
          )}

          {(role === "admin" || (retryable && item.status !== "Failed")) && (
            <DropdownMenu>
              <DropdownMenuTrigger
                render={
                  <Button variant="ghost" size="icon-sm" aria-label="More actions">
                    <MoreVertical />
                  </Button>
                }
              />
              <DropdownMenuContent>
                {retryable && item.status !== "Failed" && (
                  <DropdownMenuItem onClick={() => retry.mutate()} disabled={retry.isPending}>
                    <RotateCw />
                    Retry
                  </DropdownMenuItem>
                )}
                {role === "admin" && (
                  <DropdownMenuItem variant="destructive" onClick={() => setConfirmOpen(true)}>
                    <Trash2 />
                    Remove
                  </DropdownMenuItem>
                )}
              </DropdownMenuContent>
            </DropdownMenu>
          )}
        </div>
      </div>

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

      <RemoveIngestDialog
        open={confirmOpen}
        onOpenChange={setConfirmOpen}
        title={title}
        onConfirm={() => {
          remove.mutate();
          setConfirmOpen(false);
        }}
      />
    </div>
  );
}

function FileSummary({ files }: { files: IngestSourceFile[] }) {
  const shown = files.slice(0, 2);
  const rest = files.length - shown.length;
  return (
    <ul className="text-muted-foreground flex flex-col gap-0.5 font-mono text-xs">
      {shown.map((file) => (
        <li key={file.id} className="truncate" title={file.relativePath}>
          {file.relativePath}
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

function RemoveIngestDialog({
  open,
  onOpenChange,
  title,
  onConfirm,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  onConfirm: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Remove from pipeline?</DialogTitle>
          <DialogDescription>
            Remove <span className="text-foreground font-medium">{title}</span> from the ingest pipeline. The download and
            any published library item are left untouched.
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
