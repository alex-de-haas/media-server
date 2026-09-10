"use client";

import { useId, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowDownUp, Combine } from "lucide-react";
import { mediaServer, type LibraryMediaSource } from "@/lib/media-server";
import { formatRuntime } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
import { toast } from "@/lib/toast";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";

/** Joins consecutive parts of a movie into one new version. Source files remain available. */
export function JoinPartsControl({ sources, itemId }: { sources: LibraryMediaSource[]; itemId: string }) {
  const [open, setOpen] = useState(false);
  const { data } = useQuery({ queryKey: ["transcode-availability"], queryFn: mediaServer.transcodeAvailability });
  if (!data?.available || !data.videoPartJoining) return null;
  return <>
    <div><Button variant="outline" onClick={() => setOpen(true)}><Combine />Join parts</Button></div>
    {open && <JoinPartsDialog sources={sources} itemId={itemId} onClose={() => setOpen(false)} />}
  </>;
}

function JoinPartsDialog({ sources, itemId, onClose }: { sources: LibraryMediaSource[]; itemId: string; onClose: () => void }) {
  const id = useId();
  const [first, setFirst] = useState(sources[0]?.id ?? "");
  const [second, setSecond] = useState(sources[1]?.id ?? "");
  const client = useQueryClient();
  const selected = [first, second].map(key => sources.find(source => source.id === key));
  const valid = first !== second && selected.every(Boolean);
  const duration = selected.every(source => source && source.durationTicks > 0)
    ? selected.reduce((sum, source) => sum + source!.durationTicks, 0) : null;
  const external = selected.flatMap(source => source?.streams.filter(stream => stream.isExternal) ?? []);
  const join = useMutation({
    mutationFn: () => mediaServer.joinVideoParts([first, second]),
    onSuccess: () => {
      client.invalidateQueries({ queryKey: ["transcode-jobs"] });
      client.invalidateQueries({ queryKey: ["library-detail", itemId] });
      toast.success("Join queued. Your original files are kept.");
      onClose();
    },
  });
  return <Dialog open onOpenChange={open => { if (!open && !join.isPending) onClose(); }}>
    <DialogContent className="sm:max-w-xl">
      <DialogHeader>
        <DialogTitle>Join parts</DialogTitle>
        <DialogDescription>Play Part 1 followed by Part 2 in one new file. Both originals are kept.</DialogDescription>
      </DialogHeader>
      <div className="flex flex-col gap-4">
        {[first, second].map((value, index) => <div className="grid gap-2" key={index}>
          <Label htmlFor={`${id}-${index}`}>Part {index + 1}</Label>
          <select id={`${id}-${index}`} value={value} disabled={join.isPending}
            onChange={event => { join.reset(); (index === 0 ? setFirst : setSecond)(event.target.value); }}
            className="border-input bg-background w-full min-w-0 rounded-md border px-3 py-2 text-sm">
            {sources.map(source => <option key={source.id} value={source.id}>
              {source.fileName} · {formatRuntime(source.durationTicks)}
            </option>)}
          </select>
        </div>)}
        <Button variant="outline" className="self-start" disabled={join.isPending}
          onClick={() => { setFirst(second); setSecond(first); join.reset(); }}><ArrowDownUp />Swap parts</Button>
        <div className="bg-muted rounded-lg p-3 text-sm">
          <p className="font-medium">New version: Joined · MKV</p>
          <p>{duration ? `Expected duration: ${formatRuntime(duration)}` : "The engine will check the combined duration."}</p>
          <p className="text-muted-foreground mt-2">Compatible video, audio and subtitle tracks are copied without re-encoding. If the parts do not match, the job stops with an explanation.</p>
          <p className="text-muted-foreground mt-2">HDR, Dolby Vision, object-based audio and bitmap subtitles are not supported for joining yet.</p>
        </div>
        {external.length > 0 && <div className="text-muted-foreground text-sm">
          <p>External tracks are excluded and stay with their original parts:</p>
          <ul className="mt-1 list-inside list-disc">{external.map((stream, index) =>
            <li key={`${stream.id}-${index}`}>{stream.fileName || stream.title || `${stream.type} (${stream.language || "unknown language"})`}</li>)}</ul>
        </div>}
        {!valid && <p role="alert" className="text-destructive text-sm">Choose two different versions.</p>}
        {join.error && <p role="alert" className="text-destructive text-sm">{errorMessage(join.error)}</p>}
      </div>
      <DialogFooter>
        <Button variant="outline" disabled={join.isPending} onClick={onClose}>Cancel</Button>
        <Button disabled={!valid || join.isPending} onClick={() => join.mutate()}>{join.isPending ? "Checking parts…" : "Join parts"}</Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>;
}
