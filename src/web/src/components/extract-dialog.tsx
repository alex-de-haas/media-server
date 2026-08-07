"use client";

import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { AudioLines, Captions } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type LibraryMediaSource, type MediaStream } from "@/lib/media-server";
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

// Subtitle codecs that are pictures rather than text. They already reach the viewer by direct play from the
// container, no client reads them better as a file, and turning one into text is OCR — so the server refuses
// them and the row says so before anything is submitted.
//
// Only codecs known to be bitmap are listed. An unrecognised one stays selectable on purpose: the server owns
// the rule, and a client that is *stricter* than the API blocks a submit the API would have accepted, which
// is the one direction this check must never be wrong in.
const BITMAP_SUBTITLE_CODECS = new Set([
  "hdmv_pgs_subtitle",
  "pgssub",
  "dvd_subtitle",
  "dvdsub",
  "dvb_subtitle",
  "xsub",
]);

// What file a track becomes. Audio is always Matroska — a `.mka` carries its own language and title, so the
// file name never has to be the only record of them. Subtitles keep their own text format, which is what
// clients read off disk.
function extensionFor(stream: MediaStream): string {
  if (stream.type === "Audio") return ".mka";
  switch (stream.codec?.trim().toLowerCase()) {
    case "ass":
    case "ssa":
      return ".ass";
    case "webvtt":
    case "vtt":
      return ".vtt";
    default:
      return ".srt";
  }
}

function refusalFor(stream: MediaStream): string | null {
  const codec = stream.codec?.trim().toLowerCase();
  if (stream.type === "Subtitle" && codec && BITMAP_SUBTITLE_CODECS.has(codec)) {
    return "picture-based — already plays from the container";
  }
  return null;
}

/**
 * Composes an extraction: which of a version's own tracks are written out as files beside it.
 *
 * A dialog rather than a button, for the reason the Convert dialog is one — gigabytes start moving, and an
 * operator should see what the result will be first. It also has room to say the thing that is easy to get
 * wrong: an extracted dub is an archive, not a track a player will pick up.
 */
export function ExtractDialog({
  source,
  itemId,
  open,
  onOpenChange,
}: {
  source: LibraryMediaSource;
  itemId: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const queryClient = useQueryClient();
  const [selected, setSelected] = useState<string[]>([]);

  // The container's own tracks only. A sidecar is already a file; there is nothing to extract it from.
  const tracks = source.streams.filter(
    (stream) => !stream.isExternal && (stream.type === "Audio" || stream.type === "Subtitle"),
  );

  // Reset on every (re)open, so a dialog reopened after a submit does not arrive with the last selection.
  // Derived during render rather than in an effect, matching the Convert dialog: an effect would render the
  // stale selection once before clearing it.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) setSelected([]);
  }

  const extract = useMutation({
    mutationFn: () => mediaServer.extractTracks({ sourceId: source.id, streamIds: selected }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["transcode-jobs"] });
      queryClient.invalidateQueries({ queryKey: ["library-detail", itemId] });
      toast.success(`Extracting ${selected.length} ${selected.length === 1 ? "track" : "tracks"}`);
      onOpenChange(false);
    },
    onError: (error) => toast.error("Couldn’t start the extraction", { description: errorMessage(error) }),
  });

  const audio = tracks.filter((stream) => stream.type === "Audio");
  const subtitles = tracks.filter((stream) => stream.type === "Subtitle");

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Extract tracks to files</DialogTitle>
          <DialogDescription>
            Each track is copied into its own file beside the video and listed as a separate file on this
            version. The video itself is not touched, so the track stays in the container as well.
          </DialogDescription>
        </DialogHeader>

        <div className="flex max-h-[50vh] flex-col gap-4 overflow-y-auto">
          {tracks.length === 0 && (
            <p className="text-muted-foreground text-sm">This version has no audio or subtitle tracks to extract.</p>
          )}

          {audio.length > 0 && (
            <TrackGroup
              icon={<AudioLines className="size-3.5 shrink-0" />}
              label="Audio"
              // The caveat sits on the audio heading and not over the whole list, because it is true of audio
              // and false of subtitles: an external dub is preserved but inert until it is merged back in.
              note="— extracted as an archive; a dub only plays once merged back into a video"
              streams={audio}
              selected={selected}
              onToggle={setSelected}
            />
          )}

          {subtitles.length > 0 && (
            <TrackGroup
              icon={<Captions className="size-3.5 shrink-0" />}
              label="Subtitles"
              note="— read straight off disk by players, and editable as files"
              streams={subtitles}
              selected={selected}
              onToggle={setSelected}
            />
          )}
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button disabled={selected.length === 0 || extract.isPending} onClick={() => extract.mutate()}>
            {extract.isPending
              ? "Starting…"
              : `Extract ${selected.length || ""} ${selected.length === 1 ? "track" : "tracks"}`.trim()}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function TrackGroup({
  icon,
  label,
  note,
  streams,
  selected,
  onToggle,
}: {
  icon: React.ReactNode;
  label: string;
  note: string;
  streams: MediaStream[];
  selected: string[];
  onToggle: (next: (current: string[]) => string[]) => void;
}) {
  return (
    <div>
      <p className="text-muted-foreground flex items-center gap-1.5 text-xs">
        {icon}
        <span className="font-medium">{label}</span>
        <span>{note}</span>
      </p>
      <ul className="mt-1 flex flex-col gap-1">
        {streams.map((stream) => {
          const refusal = refusalFor(stream);
          return (
            <li key={stream.id} className="flex items-start gap-2 text-sm">
              <Checkbox
                className="mt-1"
                disabled={refusal !== null}
                checked={selected.includes(stream.id)}
                onCheckedChange={(checked) =>
                  onToggle((current) =>
                    checked ? [...current, stream.id] : current.filter((id) => id !== stream.id),
                  )
                }
                aria-label={`Extract ${stream.displayTitle ?? stream.title ?? `track ${stream.index}`}`}
              />
              <span className="min-w-0 flex-1">
                <span className="block truncate leading-6">
                  {stream.displayTitle ?? stream.codec ?? stream.title ?? `Track ${stream.index}`}
                  {stream.title && stream.title !== stream.displayTitle ? (
                    <span className="text-muted-foreground"> “{stream.title}”</span>
                  ) : null}
                </span>
                <span className="text-muted-foreground block truncate font-mono text-xs">
                  {refusal ?? extensionFor(stream)}
                </span>
              </span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
