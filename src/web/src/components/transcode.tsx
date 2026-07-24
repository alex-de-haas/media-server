"use client";

import { useId, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Trash2, X } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type CreateTranscodeInput, type LibraryMediaSource, type MediaStream, type TranscodeJob } from "@/lib/media-server";
import { formatBytes, formatEta, formatPercent, formatTimeAgo } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
import { ActivityCard, ActivityCardHeader, ActivityProgress, ActivityQueued, ActivityStats, IconAction } from "@/components/activity-card";
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
import { Field, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

const MODES = [
  { value: "encode", label: "Re-encode — smaller file" },
  { value: "copy", label: "Keep original video — lossless, HDR-safe" },
];

const CODECS = [
  { value: "hevc", label: "HEVC (H.265) — smaller files" },
  { value: "h264", label: "H.264 — most compatible" },
];

const HARDWARE = [
  { value: "auto", label: "Auto (GPU if available)" },
  { value: "vaapi", label: "VAAPI — GPU" },
  { value: "none", label: "Software — CPU" },
];

const RESOLUTIONS = [
  { value: "source", label: "Keep original" },
  { value: "2160", label: "2160p (UHD)" },
  { value: "1080", label: "1080p (FHD)" },
  { value: "720", label: "720p (HD)" },
  { value: "480", label: "480p (SD)" },
];

const ACTIVE_STATES = ["Queued", "Running"];

export function isTranscodeActive(job: TranscodeJob): boolean {
  return ACTIVE_STATES.includes(job.state);
}

/** Dialog to start a transcode of one movie source into a new version: re-encode (optionally smaller) or a
 * lossless remux, with per-track audio/subtitle selection and a choice of default track. */
export function TranscodeDialog({
  source,
  open,
  onOpenChange,
}: {
  source: LibraryMediaSource;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const audioStreams = source.streams.filter((stream) => stream.type === "Audio");
  const subtitleStreams = source.streams.filter((stream) => stream.type === "Subtitle");
  const sourceDefaultAudio = audioStreams.find((stream) => stream.isDefault)?.index ?? audioStreams[0]?.index ?? null;
  const sourceDefaultSubtitle = subtitleStreams.find((stream) => stream.isDefault)?.index ?? null;
  const hdr = source.streams.find((stream) => stream.type === "Video" && stream.hdrFormat)?.hdrFormat ?? null;
  const hdrWarning = hdr?.includes("Dolby Vision")
    ? `This source is ${hdr}. Re-encoding drops the Dolby Vision (and any HDR10+) layer — choose “Keep original video” to preserve it.`
    : `This source is ${hdr}. Re-encoding won’t carry its HDR metadata — choose “Keep original video” to preserve it.`;

  const queryClient = useQueryClient();
  const modeId = useId();
  const codecId = useId();
  const hardwareId = useId();
  const resolutionId = useId();
  const crfId = useId();

  const [mode, setMode] = useState("encode");
  const [codec, setCodec] = useState("hevc");
  const [hardware, setHardware] = useState("auto");
  const [resolution, setResolution] = useState("source");
  const [crf, setCrf] = useState("");
  const [audioKept, setAudioKept] = useState<Set<number>>(() => new Set(audioStreams.map((stream) => stream.index)));
  const [subtitleKept, setSubtitleKept] = useState<Set<number>>(() => new Set(subtitleStreams.map((stream) => stream.index)));
  const [defaultAudio, setDefaultAudio] = useState<number | null>(sourceDefaultAudio);
  const [defaultSubtitle, setDefaultSubtitle] = useState<number | null>(sourceDefaultSubtitle);

  // Reset the form each time the dialog (re)opens so a previous run's choices don't leak in.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setMode("encode");
      setCodec("hevc");
      setHardware("auto");
      setResolution("source");
      setCrf("");
      setAudioKept(new Set(audioStreams.map((stream) => stream.index)));
      setSubtitleKept(new Set(subtitleStreams.map((stream) => stream.index)));
      setDefaultAudio(sourceDefaultAudio);
      setDefaultSubtitle(sourceDefaultSubtitle);
    }
  }

  const toggleAudio = (index: number, checked: boolean) => {
    setAudioKept((prev) => {
      const next = new Set(prev);
      if (checked) {
        next.add(index);
      } else {
        next.delete(index);
      }
      return next;
    });
    // Dropping the track that was the default leaves the type with no explicit default.
    if (!checked && defaultAudio === index) {
      setDefaultAudio(null);
    }
  };

  const toggleSubtitle = (index: number, checked: boolean) => {
    setSubtitleKept((prev) => {
      const next = new Set(prev);
      if (checked) {
        next.add(index);
      } else {
        next.delete(index);
      }
      return next;
    });
    if (!checked && defaultSubtitle === index) {
      setDefaultSubtitle(null);
    }
  };

  const convert = useMutation({
    mutationFn: () => {
      const isCopy = mode === "copy";
      const keptAudio = audioStreams.filter((stream) => audioKept.has(stream.index)).map((stream) => stream.index);
      const keptSubtitles = subtitleStreams.filter((stream) => subtitleKept.has(stream.index)).map((stream) => stream.index);
      const audioDefaultChanged = defaultAudio != null && defaultAudio !== sourceDefaultAudio;
      const subtitleDefaultChanged = defaultSubtitle != null && defaultSubtitle !== sourceDefaultSubtitle;
      // Only send an explicit list when the selection is a subset or the default moved — otherwise let the
      // backend copy every track (the robust "0:a?" path).
      const audioChanged = keptAudio.length !== audioStreams.length || audioDefaultChanged;
      const subtitlesChanged = keptSubtitles.length !== subtitleStreams.length || subtitleDefaultChanged;

      const input: CreateTranscodeInput = {
        sourceId: source.id,
        videoCodec: isCopy ? "copy" : codec,
        hardwareAcceleration: isCopy ? undefined : hardware,
        // CRF only applies to software encoding; the backend ignores it otherwise.
        crf: !isCopy && hardware === "none" && crf.trim() ? Number(crf) : null,
        maxHeight: !isCopy && resolution !== "source" ? Number(resolution) : null,
        audioStreamIndexes: audioChanged ? keptAudio : undefined,
        subtitleStreamIndexes: subtitlesChanged ? keptSubtitles : undefined,
        defaultAudioStreamIndex: audioDefaultChanged ? defaultAudio : undefined,
        defaultSubtitleStreamIndex: subtitleDefaultChanged ? defaultSubtitle : undefined,
      };
      return mediaServer.createTranscodeJob(input);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["transcode-jobs"] });
      onOpenChange(false);
      toast.success("Transcode started", { description: "The new version will appear here when it’s ready." });
    },
    onError: (error) => toast.error("Couldn’t start transcode", { description: errorMessage(error) }),
  });

  const isCopy = mode === "copy";

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Convert version</DialogTitle>
          <DialogDescription>Create a new version of this movie. The original stays until you delete it.</DialogDescription>
        </DialogHeader>

        <form
          className="flex max-h-[70vh] flex-col gap-3 overflow-y-auto text-sm"
          onSubmit={(e) => {
            e.preventDefault();
            convert.mutate();
          }}
        >
          <p className="text-muted-foreground text-xs">
            Source: <span className="font-mono">{source.container}</span> · {formatBytes(source.sizeBytes)}
            {source.versionName ? ` · ${source.versionName}` : ""}
          </p>

          <Field>
            <FieldLabel htmlFor={modeId}>Video</FieldLabel>
            <Select value={mode} onValueChange={(value) => setMode((value as string | null) ?? "encode")} items={MODES}>
              <SelectTrigger id={modeId} className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {MODES.map((option) => (
                  <SelectItem key={option.value} value={option.value}>
                    {option.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>

          {!isCopy && hdr ? (
            <div className="flex items-start gap-2 rounded-md border border-amber-500/40 bg-amber-500/10 p-2 text-xs text-amber-600 dark:text-amber-500" role="alert">
              <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
              <span>{hdrWarning}</span>
            </div>
          ) : null}

          {!isCopy && (
            <>
              <Field>
                <FieldLabel htmlFor={codecId}>Codec</FieldLabel>
                <Select value={codec} onValueChange={(value) => setCodec((value as string | null) ?? "hevc")} items={CODECS}>
                  <SelectTrigger id={codecId} className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {CODECS.map((option) => (
                      <SelectItem key={option.value} value={option.value}>
                        {option.label}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </Field>

              <Field>
                <FieldLabel htmlFor={resolutionId}>Resolution</FieldLabel>
                <Select value={resolution} onValueChange={(value) => setResolution((value as string | null) ?? "source")} items={RESOLUTIONS}>
                  <SelectTrigger id={resolutionId} className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {RESOLUTIONS.map((option) => (
                      <SelectItem key={option.value} value={option.value}>
                        {option.label}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <p className="text-muted-foreground text-xs">Only downscales — a smaller source is left as-is.</p>
              </Field>

              <Field>
                <FieldLabel htmlFor={hardwareId}>Encoder</FieldLabel>
                <Select value={hardware} onValueChange={(value) => setHardware((value as string | null) ?? "auto")} items={HARDWARE}>
                  <SelectTrigger id={hardwareId} className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {HARDWARE.map((option) => (
                      <SelectItem key={option.value} value={option.value}>
                        {option.label}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </Field>

              {hardware === "none" && (
                <Field>
                  <FieldLabel htmlFor={crfId}>Quality (CRF, optional)</FieldLabel>
                  <Input
                    id={crfId}
                    inputMode="numeric"
                    placeholder="e.g. 23 — lower is better quality"
                    value={crf}
                    onChange={(e) => setCrf(e.target.value.replace(/[^0-9]/g, ""))}
                  />
                  <p className="text-muted-foreground text-xs">0–51. Leave blank for the encoder default.</p>
                </Field>
              )}
            </>
          )}

          <TrackList title="Audio" streams={audioStreams} kept={audioKept} onToggle={toggleAudio} defaultIndex={defaultAudio} onDefault={setDefaultAudio} />
          <TrackList
            title="Subtitles"
            streams={subtitleStreams}
            kept={subtitleKept}
            onToggle={toggleSubtitle}
            defaultIndex={defaultSubtitle}
            onDefault={setDefaultSubtitle}
          />

          <DialogFooter className="mt-2">
            <Button type="button" variant="ghost" size="sm" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" size="sm" disabled={convert.isPending}>
              {convert.isPending ? "Starting…" : "Start convert"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** One stream type's tracks: a checkbox to copy each, and a toggle to mark one kept track the default. */
function TrackList({
  title,
  streams,
  kept,
  onToggle,
  defaultIndex,
  onDefault,
}: {
  title: string;
  streams: MediaStream[];
  kept: Set<number>;
  onToggle: (index: number, checked: boolean) => void;
  defaultIndex: number | null;
  onDefault: (index: number) => void;
}) {
  if (!streams.length) {
    return null;
  }

  return (
    <Field>
      <FieldLabel>{title}</FieldLabel>
      <ul className="flex flex-col gap-1.5">
        {streams.map((stream) => {
          const checked = kept.has(stream.index);
          const label = stream.displayTitle ?? stream.codec ?? "—";
          return (
            <li key={stream.index} className="flex items-center gap-2">
              <Checkbox checked={checked} onCheckedChange={(value) => onToggle(stream.index, value === true)} aria-label={`Copy ${label}`} />
              <span className="min-w-0 flex-1 truncate leading-6">
                {label}
                {stream.title ? <span className="text-muted-foreground"> “{stream.title}”</span> : null}
              </span>
              <Button
                type="button"
                size="sm"
                variant={defaultIndex === stream.index ? "secondary" : "ghost"}
                className="h-6 shrink-0 px-2 text-xs"
                disabled={!checked}
                aria-pressed={defaultIndex === stream.index}
                onClick={() => onDefault(stream.index)}
              >
                Default
              </Button>
            </li>
          );
        })}
      </ul>
    </Field>
  );
}

/** One transcode job card: live progress while running, an outcome + dismiss once terminal. Built from the
 * shared Activity card pieces so a conversion reads exactly like a download or a move. */
export function TranscodeJobRow({ job }: { job: TranscodeJob }) {
  const queryClient = useQueryClient();
  const active = isTranscodeActive(job);
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["transcode-jobs"] });

  const cancel = useMutation({
    mutationFn: () => mediaServer.cancelTranscodeJob(job.id),
    onSuccess: invalidate,
    onError: (error) => toast.error("Couldn’t cancel", { description: errorMessage(error) }),
  });
  const remove = useMutation({
    mutationFn: () => mediaServer.removeTranscodeJob(job.id),
    onSuccess: invalidate,
    onError: (error) => toast.error("Couldn’t dismiss", { description: errorMessage(error) }),
  });

  const title = job.name ?? job.outputPath;
  // The card's meta line, matching the "catalog · added 2m ago" line on an ingest card: what this run
  // produces, and how long the job has existed. `createdAt` is when the job was queued, not when encoding
  // began (a queued job hasn't started at all), so the line says "added" like the ingest card rather than
  // claiming a start time the API doesn't report.
  const age = formatTimeAgo(job.createdAt);
  const meta = [job.videoCodec === "copy" ? "Remux" : job.videoCodec.toUpperCase(), age && `added ${age}`]
    .filter(Boolean)
    .join(" · ");

  return (
    <ActivityCard>
      <ActivityCardHeader
        title={title}
        titleAttr={title}
        meta={meta}
        // Waiting for an encoder slot — the queued line replaces the bar and stats below, like a queued move.
        note={job.state === "Queued" ? <ActivityQueued /> : undefined}
        actions={
          active ? (
            <IconAction label="Cancel" icon={<X />} pending={cancel.isPending} onClick={() => cancel.mutate()} />
          ) : (
            <IconAction label="Dismiss" icon={<Trash2 />} destructive pending={remove.isPending} onClick={() => remove.mutate()} />
          )
        }
      />
      {/* Stats below the bar (percent · speed × · ETA), mirroring the move and download cards. A terminal job
          keeps the stat line for its outcome, without a bar; a queued one says so in the header instead. */}
      {job.state === "Running" ? (
        <ActivityProgress value={job.percentComplete}>
          <ActivityStats>
            <span>{formatPercent(job.percentComplete)}</span>
            {job.speed != null && <span>{job.speed.toFixed(1)}×</span>}
            {job.etaSeconds != null && <span>ETA {formatEta(job.etaSeconds)}</span>}
          </ActivityStats>
        </ActivityProgress>
      ) : job.state === "Queued" ? null : (
        <ActivityStats tone={job.state === "Failed" ? "destructive" : "default"}>
          <span>{stateLabel(job)}</span>
        </ActivityStats>
      )}
      {job.state === "Failed" && job.error && <p className="text-destructive text-xs">{job.error}</p>}
    </ActivityCard>
  );
}

function stateLabel(job: TranscodeJob): string {
  if (job.state === "Queued") return "Queued";
  if (job.state === "Completed") return job.outputSizeBytes != null ? `Done · ${formatBytes(job.outputSizeBytes)}` : "Done";
  return job.state; // Failed / Canceled — the running case is rendered inline as split stat spans.
}
