"use client";

import { useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { MoreVertical, Pause, Play, Square, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { mediaServer, type Download } from "@/lib/media-server";
import { formatEta, formatPercent, formatSpeed } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
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
import { AddTorrentDialog } from "@/components/add-torrent-dialog";
import { useSession } from "@/components/app-shell";

// Persisted states where the content is fully downloaded (the file is usable / library-ready).
const COMPLETED_STATES = ["Completed", "Seeding", "StoppedSeeding"];

export function DownloadsSection() {
  const queryClient = useQueryClient();
  const downloads = useQuery({
    queryKey: ["downloads"],
    queryFn: mediaServer.listDownloads,
    refetchInterval: 2000,
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["downloads"] });
  const onError = (action: string) => (error: unknown) => toast.error(`Couldn’t ${action}`, { description: errorMessage(error) });
  const pause = useMutation({ mutationFn: mediaServer.pauseDownload, onSuccess: invalidate, onError: onError("pause download") });
  const resume = useMutation({ mutationFn: mediaServer.resumeDownload, onSuccess: invalidate, onError: onError("resume download") });
  const stopSeeding = useMutation({ mutationFn: mediaServer.stopSeeding, onSuccess: invalidate, onError: onError("stop seeding") });
  const remove = useMutation({
    mutationFn: ({ id, deleteFiles }: { id: string; deleteFiles: boolean }) => mediaServer.removeDownload(id, deleteFiles),
    onSuccess: () => {
      invalidate();
      toast.success("Download removed");
    },
    onError: onError("remove download"),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Downloads</CardTitle>
        <CardDescription>Live torrent progress, from intake to seeding.</CardDescription>
        <CardAction>
          <AddTorrentDialog />
        </CardAction>
      </CardHeader>
      <CardContent className="flex flex-col gap-2 text-sm">
        <QueryState query={downloads} empty="No active downloads.">
          {(items) =>
            items.map((download) => (
              <DownloadRow
                key={download.id}
                download={download}
                onPause={() => pause.mutate(download.id)}
                onResume={() => resume.mutate(download.id)}
                onStopSeeding={() => stopSeeding.mutate(download.id)}
                onRemove={(deleteFiles) => remove.mutate({ id: download.id, deleteFiles })}
              />
            ))
          }
        </QueryState>
      </CardContent>
    </Card>
  );
}

function DownloadRow({
  download,
  onPause,
  onResume,
  onStopSeeding,
  onRemove,
}: {
  download: Download;
  onPause: () => void;
  onResume: () => void;
  onStopSeeding: () => void;
  onRemove: (deleteFiles: boolean) => void;
}) {
  const { role } = useSession();
  const [confirmOpen, setConfirmOpen] = useState(false);

  const completed = COMPLETED_STATES.includes(download.state);
  const isPaused = /paus/i.test(download.engineState ?? download.state);
  const canPauseResume = download.state !== "Completed" && download.state !== "StoppedSeeding";
  const canStopSeeding = download.state === "Seeding";

  const percent = download.percentComplete ?? 0;
  const eta =
    download.downloadRateBytesPerSecond && download.sizeBytes
      ? (download.sizeBytes * (1 - percent / 100)) / download.downloadRateBytesPerSecond
      : null;

  return (
    <div className="rounded-md border p-3">
      <div className="flex items-center justify-between gap-2">
        <span className="truncate font-medium" title={download.name ?? download.infoHash}>
          {download.name ?? download.infoHash}
        </span>
        <div className="flex shrink-0 items-center gap-1">
          <Badge variant="secondary">{download.engineState ?? download.state}</Badge>
          {canPauseResume &&
            (isPaused ? (
              <IconAction label="Resume" icon={<Play />} onClick={onResume} />
            ) : (
              <IconAction label="Pause" icon={<Pause />} onClick={onPause} />
            ))}
          {canStopSeeding && <IconAction label="Stop seeding" icon={<Square />} onClick={onStopSeeding} />}
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
                <DropdownMenuItem variant="destructive" onClick={() => setConfirmOpen(true)}>
                  <Trash2 />
                  Remove
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          )}
        </div>
      </div>

      <div className="bg-secondary mt-2 h-2 w-full overflow-hidden rounded-full">
        <div className="bg-primary h-full transition-[width] duration-500" style={{ width: `${Math.min(percent, 100)}%` }} />
      </div>
      <div className="text-muted-foreground mt-2 flex flex-wrap gap-x-4 gap-y-1">
        <span>{formatPercent(download.percentComplete)}</span>
        <span>↓ {formatSpeed(download.downloadRateBytesPerSecond)}</span>
        <span>↑ {formatSpeed(download.uploadRateBytesPerSecond)}</span>
        <span>ratio {download.ratio?.toFixed(2) ?? "—"}</span>
        <span>{download.peers ?? 0} peers</span>
        <span>ETA {formatEta(eta)}</span>
      </div>

      <RemoveDownloadDialog
        open={confirmOpen}
        onOpenChange={setConfirmOpen}
        name={download.name ?? download.infoHash}
        completed={completed}
        onConfirm={(deleteFiles) => {
          onRemove(deleteFiles);
          setConfirmOpen(false);
        }}
      />
    </div>
  );
}

function IconAction({ label, icon, onClick }: { label: string; icon: ReactNode; onClick: () => void }) {
  return (
    <Tooltip>
      <TooltipTrigger
        render={
          <Button variant="ghost" size="icon-sm" aria-label={label} onClick={onClick}>
            {icon}
          </Button>
        }
      />
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}

function RemoveDownloadDialog({
  open,
  onOpenChange,
  name,
  completed,
  onConfirm,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  name: string;
  completed: boolean;
  onConfirm: (deleteFiles: boolean) => void;
}) {
  // For an unfinished download only a partial file exists on disk, so default to removing it; for a
  // completed one the library item is published from these files, so default to keeping them.
  const [deleteFiles, setDeleteFiles] = useState(!completed);

  // Re-apply that default every time the dialog (re)opens, so a prior toggle (then cancel) doesn't
  // carry over to the next open. Adjusting during render avoids an effect's cascading re-render.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) setDeleteFiles(!completed);
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Remove download?</DialogTitle>
          <DialogDescription>
            Remove <span className="text-foreground font-medium">{name}</span> from the downloads list.
          </DialogDescription>
        </DialogHeader>

        <label className="flex items-start gap-2 rounded-md border p-3 text-sm">
          <Checkbox
            className="mt-0.5"
            checked={deleteFiles}
            onCheckedChange={(checked) => setDeleteFiles(checked === true)}
          />
          <span>
            Delete files from disk
            <span className="text-muted-foreground block text-xs">
              {completed
                ? "Also removes the published library item and its media files."
                : "Removes the partially-downloaded files."}
            </span>
          </span>
        </label>

        <DialogFooter>
          <Button type="button" variant="ghost" size="sm" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button type="button" variant="destructive" size="sm" onClick={() => onConfirm(deleteFiles)}>
            {deleteFiles ? "Remove + delete files" : "Remove"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
