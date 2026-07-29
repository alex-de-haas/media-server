"use client";

import { useId, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
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
  preselectedSidecars,
}: {
  source: LibraryMediaSource;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Sidecar stream ids to open with already checked — how the Media tab's Merge button arrives here. */
  preselectedSidecars?: string[];
}) {
  const audioStreams = source.streams.filter((stream) => stream.type === "Audio" && !stream.isExternal);
  const subtitleStreams = source.streams.filter((stream) => stream.type === "Subtitle" && !stream.isExternal);
  // The files beside the video. They are not tracks of this container — each is an input of its own — so
  // they carry ids rather than indexes and are selected separately from the two lists above.
  const sidecars = source.streams.filter((stream) => stream.isExternal);
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

  // Opening from Merge means the operator already said "fold these in", and folding in is a copy unless
  // they ask for more — so the dialog opens on "keep the video" rather than silently proposing a re-encode
  // of a file they only wanted more tracks in.
  const initialMode = preselectedSidecars?.length ? "copy" : "encode";
  const [mode, setMode] = useState(initialMode);
  const [codec, setCodec] = useState("hevc");
  const [hardware, setHardware] = useState("auto");
  const [resolution, setResolution] = useState("source");
  const [crf, setCrf] = useState("");
  // Names and languages the operator corrected, keyed by stream id. Only what actually changed is sent: an
  // untouched field keeps whatever the source stream carries, so correcting one track never freezes the
  // others' metadata.
  const [titles, setTitles] = useState<Record<string, string>>({});
  const [languages, setLanguages] = useState<Record<string, string>>({});
  const [audioKept, setAudioKept] = useState<Set<number>>(() => new Set(audioStreams.map((stream) => stream.index)));
  const [subtitleKept, setSubtitleKept] = useState<Set<number>>(() => new Set(subtitleStreams.map((stream) => stream.index)));
  const [merged, setMerged] = useState<Set<string>>(() => new Set(preselectedSidecars ?? []));
  const [defaultAudio, setDefaultAudio] = useState<number | null>(sourceDefaultAudio);
  const [defaultSubtitle, setDefaultSubtitle] = useState<number | null>(sourceDefaultSubtitle);

  // Reset the form each time the dialog (re)opens so a previous run's choices don't leak in.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setMode(initialMode);
      setCodec("hevc");
      setHardware("auto");
      setResolution("source");
      setCrf("");
      setTitles({});
      setLanguages({});
      setAudioKept(new Set(audioStreams.map((stream) => stream.index)));
      setSubtitleKept(new Set(subtitleStreams.map((stream) => stream.index)));
      setMerged(new Set(preselectedSidecars ?? []));
      setDefaultAudio(sourceDefaultAudio);
      setDefaultSubtitle(sourceDefaultSubtitle);
    }
  }

  // The tags a language may be corrected to, from the service that validates them. Fetched once the dialog
  // is open and only for as long as it stays a fixed list — an empty answer means "don't judge", so a failed
  // fetch leaves the field permissive and lets the API have the last word rather than blocking a submit.
  const { data: knownLanguages } = useQuery({
    queryKey: ["transcode-languages"],
    queryFn: mediaServer.transcodeLanguages,
    staleTime: Infinity,
    enabled: open,
  });

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

  const toggleSidecar = (id: string, checked: boolean) => {
    setMerged((prev) => {
      const next = new Set(prev);
      if (checked) {
        next.add(id);
      } else {
        next.delete(id);
      }
      return next;
    });
  };

  // A language the API will refuse, flagged before the whole form is submitted rather than after. An empty
  // list (the fetch failed, or has not landed) judges nothing.
  const badLanguages = Object.entries(languages).filter(
    ([, value]) => value.trim() !== "" && knownLanguages?.length && !knownLanguages.includes(value.trim().toLowerCase()),
  );

  const convert = useMutation({
    mutationFn: () => {
      const isCopy = mode === "copy";
      const mergeStreamIds = sidecars.filter((stream) => merged.has(stream.id)).map((stream) => stream.id);
      const keptAudio = audioStreams.filter((stream) => audioKept.has(stream.index)).map((stream) => stream.index);
      const keptSubtitles = subtitleStreams.filter((stream) => subtitleKept.has(stream.index)).map((stream) => stream.index);
      const audioDefaultChanged = defaultAudio != null && defaultAudio !== sourceDefaultAudio;
      const subtitleDefaultChanged = defaultSubtitle != null && defaultSubtitle !== sourceDefaultSubtitle;
      // Only send an explicit list when the selection is a subset or the default moved — otherwise let the
      // backend copy every track (the robust "0:a?" path).
      //
      // A merge is the exception: an appended track's output position is only knowable when the primary
      // list is explicit, so the engine refuses to relabel one otherwise. Sending the full list costs
      // nothing (it maps to exactly the same streams) and keeps a merged dub's name editable.
      const merging = mergeStreamIds.length > 0;
      const audioChanged = merging || keptAudio.length !== audioStreams.length || audioDefaultChanged;
      const subtitlesChanged = merging || keptSubtitles.length !== subtitleStreams.length || subtitleDefaultChanged;

      // Only tracks that end up in the output can carry an edit: an unchecked sidecar has no output stream
      // to write to, and the API refuses the edit rather than dropping it silently.
      const editable = new Set([
        ...audioStreams.map((stream) => stream.id),
        ...subtitleStreams.map((stream) => stream.id),
        ...mergeStreamIds,
      ]);
      const metadataEdits = [...editable]
        .map((streamId) => {
          const stream = source.streams.find((candidate) => candidate.id === streamId);
          const title = titles[streamId]?.trim();
          const language = languages[streamId]?.trim().toLowerCase();
          return {
            streamId,
            // Undefined is "keep what the source has" — so only a value that actually differs is sent, and
            // correcting one field never freezes the other.
            title: title !== undefined && title !== (stream?.title ?? "") ? title : undefined,
            language: language !== undefined && language !== (stream?.language ?? "") ? language : undefined,
          };
        })
        .filter((edit) => edit.title !== undefined || edit.language !== undefined);

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
        mergeStreamIds: merging ? mergeStreamIds : undefined,
        metadataEdits,
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
  const mergeCount = sidecars.filter((stream) => merged.has(stream.id)).length;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {/* Wider than the default: a release with three dubs and two subtitle tracks is eight rows of
          controls, each carrying a name and a language field. */}
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Convert version</DialogTitle>
          <DialogDescription>Create a new version of this movie. The original stays until you delete it.</DialogDescription>
        </DialogHeader>

        <form
          // The scroll container reaches the dialog's edge (-mr-6 cancels its padding) and re-pads its own
          // content, so the scrollbar gets a gutter of its own instead of running down the side of the
          // inputs — while the fields stay inset the same 24px as the header above them.
          className="-mr-6 flex max-h-[70vh] flex-col gap-3 overflow-y-auto pr-6 text-sm"
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

          <TrackList
            title="Audio"
            streams={audioStreams}
            isKept={(stream) => audioKept.has(stream.index)}
            onToggle={(stream, checked) => toggleAudio(stream.index, checked)}
            defaultIndex={defaultAudio}
            onDefault={setDefaultAudio}
            titles={titles}
            onTitle={setTitles}
            languages={languages}
            onLanguage={setLanguages}
            knownLanguages={knownLanguages}
          />
          <TrackList
            title="Subtitles"
            streams={subtitleStreams}
            isKept={(stream) => subtitleKept.has(stream.index)}
            onToggle={(stream, checked) => toggleSubtitle(stream.index, checked)}
            defaultIndex={defaultSubtitle}
            onDefault={setDefaultSubtitle}
            titles={titles}
            onTitle={setTitles}
            languages={languages}
            onLanguage={setLanguages}
            knownLanguages={knownLanguages}
          />
          <TrackList
            title="Files beside this version"
            description="Each is folded into the output as an extra track. The files stay on disk."
            streams={sidecars}
            isKept={(stream) => merged.has(stream.id)}
            onToggle={(stream, checked) => toggleSidecar(stream.id, checked)}
            titles={titles}
            onTitle={setTitles}
            languages={languages}
            onLanguage={setLanguages}
            knownLanguages={knownLanguages}
          />

          <DialogFooter className="mt-2">
            <Button type="button" variant="ghost" size="sm" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" size="sm" disabled={convert.isPending || badLanguages.length > 0}>
              {convert.isPending
                ? "Starting…"
                : mergeCount > 0
                  ? `Start convert + merge ${mergeCount}`
                  : "Start convert"}
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
  description,
  streams,
  isKept,
  onToggle,
  defaultIndex,
  onDefault,
  titles,
  onTitle,
  languages,
  onLanguage,
  knownLanguages,
}: {
  title: string;
  description?: string;
  streams: MediaStream[];
  /** Whether a track is kept. A predicate rather than a set, because an embedded track is selected by its
   *  index inside the container and a sidecar — a file, with no index here — by its id. */
  isKept: (stream: MediaStream) => boolean;
  onToggle: (stream: MediaStream, checked: boolean) => void;
  defaultIndex?: number | null;
  onDefault?: (index: number) => void;
  titles: Record<string, string>;
  onTitle: (update: (current: Record<string, string>) => Record<string, string>) => void;
  languages: Record<string, string>;
  onLanguage: (update: (current: Record<string, string>) => Record<string, string>) => void;
  knownLanguages: string[] | undefined;
}) {
  if (!streams.length) {
    return null;
  }

  return (
    <Field>
      <FieldLabel>{title}</FieldLabel>
      {description ? <p className="text-muted-foreground text-xs">{description}</p> : null}
      <ul className="flex flex-col gap-2">
        {streams.map((stream) => {
          const checked = isKept(stream);
          // A sidecar routinely carries neither codec nor language — its release name is all it has.
          const label = stream.displayTitle ?? stream.codec ?? stream.title?.trim() ?? stream.fileName ?? "—";
          const language = languages[stream.id] ?? stream.language ?? "";
          const badLanguage =
            language.trim() !== "" && !!knownLanguages?.length && !knownLanguages.includes(language.trim().toLowerCase());
          return (
            <li key={stream.id} className="flex flex-col gap-1">
              <div className="flex items-center gap-2">
                <Checkbox checked={checked} onCheckedChange={(value) => onToggle(stream, value === true)} aria-label={`Copy ${label}`} />
                <span className="min-w-0 flex-1 truncate leading-6">{label}</span>
                {stream.fileName ? (
                  <span className="text-muted-foreground hidden shrink-0 font-mono text-xs sm:inline">{stream.fileName}</span>
                ) : null}
                {onDefault ? (
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
                ) : null}
              </div>
              {/* The name and language written into the output. Editing is offered here because a job is
                  being submitted anyway — changing metadata alone would still rewrite the whole file.
                  Indented under the checkbox with padding rather than a margin: a w-full input plus a margin
                  is wider than its row, which is what used to push these past the dialog's edge. */}
              <div className="flex gap-2 pl-6">
                <Input
                  value={titles[stream.id] ?? stream.title ?? ""}
                  onChange={(event) => onTitle((current) => ({ ...current, [stream.id]: event.target.value }))}
                  disabled={!checked}
                  placeholder="Track name"
                  aria-label={`Name for ${label}`}
                  className="h-7 min-w-0 flex-1 text-xs"
                />
                <Input
                  value={language}
                  onChange={(event) => onLanguage((current) => ({ ...current, [stream.id]: event.target.value }))}
                  disabled={!checked}
                  aria-invalid={badLanguage}
                  placeholder="Language"
                  aria-label={`Language for ${label}`}
                  className="h-7 w-28 shrink-0 text-xs"
                />
              </div>
              {badLanguage ? (
                <p className="text-destructive pl-6 text-xs">
                  “{language.trim()}” isn’t an ISO 639-2 tag — try “rus”, “eng”.
                </p>
              ) : null}
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
