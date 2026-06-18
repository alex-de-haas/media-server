"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { mediaServer, type Download } from "@/lib/media-server";
import { formatEta, formatPercent, formatSpeed } from "@/lib/format";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { QueryState } from "@/components/states";
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
  const pause = useMutation({ mutationFn: mediaServer.pauseDownload, onSuccess: invalidate });
  const resume = useMutation({ mutationFn: mediaServer.resumeDownload, onSuccess: invalidate });
  const stopSeeding = useMutation({ mutationFn: mediaServer.stopSeeding, onSuccess: invalidate });
  const remove = useMutation({
    mutationFn: ({ id, deleteFiles }: { id: string; deleteFiles: boolean }) => mediaServer.removeDownload(id, deleteFiles),
    onSuccess: invalidate,
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Downloads</CardTitle>
        <CardDescription>
          Live torrent progress. &ldquo;Remove (keep files)&rdquo; leaves your library intact; &ldquo;Delete + remove files&rdquo; also removes the published item.
        </CardDescription>
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
  const completed = COMPLETED_STATES.includes(download.state);
  const percent = download.percentComplete ?? 0;
  const eta =
    download.downloadRateBytesPerSecond && download.sizeBytes
      ? ((download.sizeBytes * (1 - percent / 100)) / download.downloadRateBytesPerSecond)
      : null;

  return (
    <div className="rounded-md border p-3">
      <div className="flex items-center justify-between gap-2">
        <span className="truncate font-medium">{download.name ?? download.infoHash}</span>
        <Badge variant="secondary">{download.engineState ?? download.state}</Badge>
      </div>
      <div className="bg-secondary mt-2 h-2 w-full overflow-hidden rounded-full">
        <div className="bg-primary h-full" style={{ width: `${Math.min(percent, 100)}%` }} />
      </div>
      <div className="text-muted-foreground mt-2 flex flex-wrap gap-x-4 gap-y-1">
        <span>{formatPercent(download.percentComplete)}</span>
        <span>↓ {formatSpeed(download.downloadRateBytesPerSecond)}</span>
        <span>↑ {formatSpeed(download.uploadRateBytesPerSecond)}</span>
        <span>ratio {download.ratio?.toFixed(2) ?? "—"}</span>
        <span>{download.peers ?? 0} peers</span>
        <span>ETA {formatEta(eta)}</span>
      </div>
      <div className="mt-2 flex flex-wrap gap-2">
        <Button variant="outline" size="sm" onClick={onResume}>Resume</Button>
        <Button variant="outline" size="sm" onClick={onPause}>Pause</Button>
        <Button variant="outline" size="sm" onClick={onStopSeeding}>Stop seeding</Button>
        {role === "admin" &&
          (completed ? (
            <>
              <Button variant="ghost" size="sm" onClick={() => onRemove(false)}>Remove (keep files)</Button>
              <Button variant="ghost" size="sm" className="text-destructive" onClick={() => onRemove(true)}>Delete + remove files</Button>
            </>
          ) : (
            // While unfinished, the only file on disk is a partial; never offer a keep-files removal.
            <Button variant="ghost" size="sm" className="text-destructive" onClick={() => onRemove(true)}>Delete (removes files)</Button>
          ))}
      </div>
    </div>
  );
}
