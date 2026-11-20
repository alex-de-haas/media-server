"use client";
import { audioBitrateFor, compositionReason, encoderQualityLabel, isValidEncoderQuality, selectedOutputEstimate, trackJobInput, trackLabel, versionNameError, type TrackOperation } from "@/lib/track-actions";
import { ApiError } from "@/lib/api";
import { ManagementJobLink } from "@/components/management-job-link";

import { useEffect, useId, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Trash2, X } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type EncoderQualityOption, type LibraryMediaSource, type MediaStream, type TranscodeJob } from "@/lib/media-server";
import { formatBytes, formatEta, formatPercent, formatTimeAgo, objectAudioFormat, reencodeDynamicRangeWarning } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
import { ActivityCard, ActivityCardHeader, ActivityProgress, ActivityQueued, ActivityStats, IconAction } from "@/components/activity-card";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
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
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Field, FieldContent, FieldDescription, FieldError, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

const CODECS = [
  { value: "hevc", label: "HEVC (H.265) — smaller files" },
  { value: "h264", label: "H.264 — most compatible" },
];

const HARDWARE = [
  { value: "auto", label: "Auto (GPU if available)" },
  { value: "vaapi", label: "VAAPI — GPU" },
  { value: "none", label: "Software — CPU" },
];

const ENCODER_LABELS = {
  none: "Software — CPU",
  vaapi: "VAAPI — GPU",
  amf: "AMD AMF — GPU",
  videotoolbox: "VideoToolbox — GPU",
} satisfies Record<EncoderQualityOption["hardwareAcceleration"], string>;

// These presets are used with an older engine that has no native quality options.
const QUALITY = [
  { value: "highest", label: "Highest" },
  { value: "high", label: "High" },
  { value: "balanced", label: "Balanced" },
  { value: "small", label: "Small" },
];

function encoderQualityHint(option: EncoderQualityOption) {
  const hevc = option.videoCodec === "hevc";
  if (option.parameter === "crf") {
    return hevc
      ? "Lower CRF usually keeps more fine detail and produces a larger file. CRF 18 was the previous Highest preset. Try 16 or 14 for more texture and grain."
      : "Lower CRF usually keeps more fine detail and produces a larger file. CRF 16 was the previous Highest preset.";
  }
  if (option.parameter === "qp") {
    return `Lower QP usually keeps more fine detail and produces a larger file. QP ${hevc ? 22 : 20} was the previous Highest preset.`;
  }
  return `Higher q:v usually keeps more fine detail and produces a larger file. q:v ${hevc ? 70 : 75} was the previous Highest preset.`;
}

function EncoderQualityField({ id, option, input, value, disabled, onChange }: {
  id: string;
  option: EncoderQualityOption;
  input: string;
  value: number | undefined;
  disabled: boolean;
  onChange: (value: string) => void;
}) {
  const { min, max, defaultValue } = option;
  const parameter = encoderQualityLabel(option.parameter);
  const valid = isValidEncoderQuality(option, value);
  const reflect = (position: number) => option.parameter === "q:v" ? position : min + max - position;
  const step = Math.max(1, option.step);
  return (
    <Field className="col-span-2" data-invalid={!valid}>
      <FieldLabel htmlFor={id}>Quality ({parameter})</FieldLabel>
      <div className="flex items-start gap-3">
        <div className="min-w-0 flex-1 pt-2">
          <input
            type="range"
            aria-label={`${parameter} slider`}
            aria-describedby={`${id}-help`}
            aria-valuetext={`${parameter} ${valid ? value : defaultValue}`}
            className="block w-full accent-brand"
            min={min}
            max={max}
            step={step}
            value={reflect(valid ? value : defaultValue)}
            disabled={disabled}
            onChange={event => onChange(String(reflect(event.target.valueAsNumber)))}
          />
          <div className="mt-1 flex justify-between gap-2 text-xs text-muted-foreground">
            <span>More compression</span>
            <span>More detail</span>
          </div>
        </div>
        <Input
          id={id}
          aria-describedby={`${id}-help`}
          aria-invalid={!valid}
          className="w-24 shrink-0"
          type="number"
          inputMode={option.parameter === "crf" ? "decimal" : "numeric"}
          min={min}
          max={max}
          step={option.step}
          value={input}
          disabled={disabled}
          onChange={event => onChange(event.target.value)}
        />
      </div>
      {valid ? <FieldDescription id={`${id}-help`}>Range {min}–{max}. {encoderQualityHint(option)}</FieldDescription>
        : <FieldError id={`${id}-help`}>Enter a value for {parameter} from {min} to {max} in steps of {option.step}.</FieldError>}
    </Field>
  );
}

/** What one track weighs, at the bitrate the probe recorded for it. Null when the file stated none — the
 * caller then says nothing rather than carving up the source's overall rate, which measures the file and
 * not this track. */
function streamBytes(stream: MediaStream, durationSeconds: number): number | null {
  return stream.bitrate ? (durationSeconds * stream.bitrate) / 8 : null;
}

/** What re-encoding this track buys and what it costs. Both sides are bitrate times duration, so they
 * compare like for like; a source that recorded no bitrate for the track simply has no "before" to show and
 * the row leads with the result. The downmix note is not a footnote: E-AC-3 stops at 5.1, and losing the
 * height channels is a real loss that is invisible in the output. */
function audioReEncodeHint(stream: MediaStream, durationSeconds: number): string {
  const kbps = audioBitrateFor(stream.channels);
  const size = formatBytes((durationSeconds * kbps * 1000) / 8);
  const channels = stream.channels ?? 2;
  const layout = channels > 6 ? `${channels} channels → 5.1` : `${channels} channels`;
  const before = streamBytes(stream, durationSeconds);
  // Losing the object layer outweighs every other number on this line, so it leads. ffmpeg encodes neither
  // JOC nor DTS:X, so there is no bitrate at which this track keeps them — the only way to keep them is to
  // copy the track.
  // The separator only earns its place when a "before" size follows it to be separated from; with no
  // recorded bitrate the arrow reads straight off the loss note — "drops Atmos → E-AC-3, …" — rather than
  // leaving a dangling "· →".
  const objects = objectAudioFormat(stream.profile);
  const loss = objects ? `drops ${objects} ${before ? "· " : ""}` : "";
  return `${loss}${before ? `${formatBytes(before)} ` : ""}→ E-AC-3, ${layout}, ${kbps} kbps · about ${size}`;
}

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

/** A confirmation for the grid's fixed selection. Mount afresh for every operation. */
export function TranscodeDialog({ source, selected, operation, onClose, onSubmitted }: {
  source: LibraryMediaSource;
  selected: MediaStream[];
  operation: TrackOperation;
  onClose: () => void;
  onSubmitted: () => void;
}) {
  const copy = operation === "remux";
  const label = copy ? "Remux" : "Convert";
  const [versionName, setVersionName] = useState(label);
  const [outputMountLabel, setOutputMountLabel] = useState("");
  const destinations = useQuery({ queryKey: ["transcode-destinations"], queryFn: mediaServer.transcodeDestinations });
  const destinationItems = [{ value: "", label: "New library version (writable catalogs only)" },
    ...(destinations.data ?? []).map(value => ({ value, label: "Export to " + value }))];
  const versionInput = useRef<HTMLInputElement>(null);
  const [codecChoice, setCodecChoice] = useState("");
  const [encoderChoice, setEncoderChoice] = useState("");
  const [resolution, setResolution] = useState("source");
  const [quality, setQuality] = useState("high");
  const [qualityChoice, setQualityChoice] = useState<string | null>(null);
  const [titles, setTitles] = useState<Record<string, string>>({});
  const [reencoded, setReencoded] = useState<string[]>([]);
  const [dolbyVision, setDolbyVision] = useState(false);
  const queryClient = useQueryClient();
  const id = useId();
  const video = selected.find(stream => stream.type === "Video");
  const { data: availability } = useQuery({ queryKey: ["transcode-availability"], queryFn: mediaServer.transcodeAvailability, staleTime: 5 * 60 * 1000 });
  const options = copy ? null : availability?.encoderQualityOptions;
  const nativeQuality = options != null;
  const codecItems = nativeQuality ? CODECS.filter(item => options.some(option => option.videoCodec === item.value)) : CODECS;
  const codec = nativeQuality ? codecItems.find(item => item.value === codecChoice)?.value ?? "" : codecChoice || "hevc";
  const encoderOptions = options?.filter(option => option.videoCodec === codec);
  const encoderItems = encoderOptions?.map(option => ({
    value: option.hardwareAcceleration,
    label: ENCODER_LABELS[option.hardwareAcceleration],
  })) ?? HARDWARE;
  const selectedEncoder = encoderOptions?.find(option => option.hardwareAcceleration === encoderChoice);
  const hardware = nativeQuality ? selectedEncoder?.hardwareAcceleration ?? "" : encoderChoice || "auto";
  const qualityInput = qualityChoice ?? (selectedEncoder ? String(selectedEncoder.defaultValue) : "");
  const qualityValue = qualityInput === "" ? undefined : Number(qualityInput);
  const encoderQuality = selectedEncoder && isValidEncoderQuality(selectedEncoder, qualityValue)
    ? { option: selectedEncoder, value: qualityValue }
    : undefined;
  const needsEncoderQuality = nativeQuality && !encoderQuality;
  const profile7 = video?.dolbyVision?.profile === 7;
  const dvEligible = profile7 && source.container.toLowerCase() === "mkv" && availability?.dolbyVisionConversion;
  const warning = !copy ? reencodeDynamicRangeWarning(video?.hdrFormat ?? null, video?.dolbyVision) : null;
  const settings = { operation, versionName, titles, codec, hardware, quality, encoderQuality, resolution, reencoded, dolbyVision: !!dvEligible && dolbyVision };
  const invalidName = versionNameError(versionName);
  const estimate = nativeQuality ? null : selectedOutputEstimate(source, selected, settings);
  const reason = compositionReason(selected) ?? (!availability?.available ? "Transcode Engine is unavailable."
    : !availability.explicitVideoSelection ? "Update Transcode Engine to use track selection."
    : codecItems.length === 0 ? "No video encoder is available." : null);

  function selectCodec(value: string) {
    setCodecChoice(value);
    if (nativeQuality) {
      setEncoderChoice("");
      setQualityChoice(null);
    }
  }

  function selectEncoder(value: string) {
    setEncoderChoice(value);
    setQualityChoice(null);
  }

  const submit = useMutation({
    mutationFn: () => {
      if (reason) throw new Error(reason);
      if (invalidName) throw new Error(invalidName);
      if (needsEncoderQuality) throw new Error("Choose a codec, an encoder, and a valid quality value.");
      return mediaServer.createTranscodeJob({ ...trackJobInput(source, selected, settings), outputMountLabel: outputMountLabel || undefined });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["transcode-jobs"] });
      onSubmitted();
      toast.success(`${label} started`, { description: outputMountLabel ? "Download the export from its job when it is ready." : "The new version will appear when it’s ready." });
    },
  });
  const serverNameError = submit.error instanceof ApiError && submit.error.code === "invalid_version_name" ? submit.error.message : null;
  const nameError = invalidName ?? serverNameError;
  useEffect(() => { if (serverNameError) versionInput.current?.focus(); }, [serverNameError]);
  return (
    <Dialog open onOpenChange={open => { if (!open && !submit.isPending) onClose(); }}>
      <DialogContent className="sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>{label} selected tracks</DialogTitle>
          <DialogDescription>{copy ? "Copy the selected tracks into a new MKV, with optional E-AC-3 audio conversion." : "Re-encode the selected video and include the selected audio and subtitles."} The original files stay unchanged.</DialogDescription>
        </DialogHeader>
        <form className="flex max-h-[70vh] flex-col gap-4 overflow-y-auto" onSubmit={event => { event.preventDefault(); if (!submit.isPending && !reason && !nameError && !needsEncoderQuality) submit.mutate(); }}>
          <p className="break-words text-xs text-muted-foreground">{source.fileName} · {selected.length} tracks</p>
          <Field>
            <FieldLabel htmlFor={id + "-destination"}>Output destination</FieldLabel>
            <Select value={outputMountLabel} onValueChange={value => setOutputMountLabel(value ?? "")} items={destinationItems}>
              <SelectTrigger id={id + "-destination"}><SelectValue /></SelectTrigger>
              <SelectContent>{destinationItems.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}</SelectContent>
            </Select>
            <FieldDescription>Read-only libraries require a separate export folder. Exports do not add a version to the library.</FieldDescription>
            {destinations.isError && <FieldError>Could not load output folders. Reopen this dialog to retry.</FieldError>}
          </Field>
          <Field data-invalid={!!nameError}>
            <FieldLabel htmlFor={`${id}-version`}>Version name</FieldLabel>
            <Input ref={versionInput} id={`${id}-version`} value={versionName} maxLength={120} disabled={submit.isPending}
              aria-invalid={!!nameError} aria-describedby={`${id}-version-help`}
              onChange={event => { setVersionName(event.target.value); if (serverNameError) submit.reset(); }} />
            {nameError ? <FieldError id={`${id}-version-help`}>{nameError}</FieldError>
              : <FieldDescription id={`${id}-version-help`}>Used after the title in the new .mkv filename.</FieldDescription>}
          </Field>
          {!copy && <div className="grid grid-cols-2 gap-3">
            {[
              { label: "Codec", value: codec, set: selectCodec, items: codecItems, placeholder: "Choose codec", disabled: codecItems.length === 0 },
              { label: "Encoder", value: hardware, set: selectEncoder, items: encoderItems, placeholder: "Choose encoder", disabled: encoderItems.length === 0 },
              { label: "Resolution", value: resolution, set: setResolution, items: RESOLUTIONS },
              ...(!nativeQuality ? [{ label: "Quality", value: quality, set: setQuality, items: QUALITY }] : []),
            ].map(field => <Field key={field.label} className={nativeQuality && field.label === "Resolution" ? "col-span-2" : undefined}>
              <FieldLabel htmlFor={`${id}-${field.label}`}>{field.label}</FieldLabel>
              <Select value={field.value || null} onValueChange={value => field.set(value ?? field.value)} items={field.items} disabled={field.disabled || submit.isPending}>
                <SelectTrigger id={`${id}-${field.label}`} className="w-full"><SelectValue placeholder={field.placeholder} /></SelectTrigger>
                <SelectContent>{field.items.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}</SelectContent>
              </Select>
            </Field>)}
            {selectedEncoder && <EncoderQualityField id={`${id}-quality`} option={selectedEncoder} input={qualityInput}
              value={qualityValue} onChange={setQualityChoice} disabled={submit.isPending} />}
          </div>}
          {warning && <p className="flex gap-2 rounded-md bg-warning/10 p-2 text-xs text-warning-foreground"><AlertTriangle className="size-4 shrink-0" />{warning}</p>}
          <div className="divide-y rounded-md border">
            {selected.map(stream => <div key={stream.id} className="flex flex-col gap-1.5 p-2">
              <Field>
                <FieldLabel htmlFor={`${id}-${stream.id}`}>{trackLabel(stream)}{stream.language ? ` · ${stream.language}` : ""}{stream.codec ? ` · ${stream.codec.toUpperCase()}` : ""}</FieldLabel>
                <Input id={`${id}-${stream.id}`} aria-label={`Name of ${trackLabel(stream)}`} value={titles[stream.id] ?? stream.title ?? ""} placeholder="Track name" disabled={submit.isPending} onChange={event => setTitles(current => ({ ...current, [stream.id]: event.target.value }))} />
              </Field>
              {stream.type === "Audio" && !stream.isExternal && <Field orientation="horizontal" data-disabled={submit.isPending}>
                <Checkbox id={`${id}-${stream.id}-eac3`} checked={reencoded.includes(stream.id)} disabled={submit.isPending}
                  aria-describedby={reencoded.includes(stream.id) ? `${id}-${stream.id}-eac3-hint` : undefined}
                  onCheckedChange={checked => setReencoded(current => checked ? [...current, stream.id] : current.filter(value => value !== stream.id))} />
                <FieldContent>
                  <FieldLabel htmlFor={`${id}-${stream.id}-eac3`}>Re-encode {trackLabel(stream)} to E-AC-3</FieldLabel>
                  {reencoded.includes(stream.id) && <FieldDescription id={`${id}-${stream.id}-eac3-hint`}>{audioReEncodeHint(stream, source.durationTicks / 10_000_000)}</FieldDescription>}
                </FieldContent>
              </Field>}
            </div>)}
          </div>
          {copy && profile7 && <div className="space-y-1.5 text-xs">
            <label className="flex items-center gap-2"><Checkbox checked={dolbyVision} disabled={!dvEligible || submit.isPending} onCheckedChange={checked => setDolbyVision(!!checked)} />Convert Dolby Vision to profile 8.1</label>
            <p className="text-muted-foreground">{dvEligible ? "Copies the HDR10 base layer and rewrites Dolby Vision metadata. The enhancement layer is discarded." : "Requires a Matroska source and an engine with Dolby Vision conversion tools."}</p>
          </div>}
          <p className="text-xs text-muted-foreground">
            Output: MKV{copy ? " · Video copied" : codec && ` · ${codec.toUpperCase()} video`}
            {!nativeQuality && (estimate !== null ? ` · approximately ${formatBytes(estimate)}` : " · size estimate unavailable")}
            {(!copy || reencoded.length > 0) && estimate !== null ? ". Actual size depends on the content." : ""}
          </p>
          {(reason || (submit.isError && !serverNameError)) && <p role="alert" className="text-sm text-destructive">{reason ?? errorMessage(submit.error)}</p>}
          <DialogFooter>
            <Button type="button" variant="outline" disabled={submit.isPending} onClick={onClose}>Cancel</Button>
            <Button type="submit" disabled={!!reason || !!nameError || needsEncoderQuality || submit.isPending}>{submit.isPending ? "Starting…" : `Start ${label.toLowerCase()}`}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

export function TranscodeJobRow({ job, showManagementLink = true }: { job: TranscodeJob; showManagementLink?: boolean }) {
  const queryClient = useQueryClient();
  const [cancelOpen, setCancelOpen] = useState(false);
  const [deleteExportOpen, setDeleteExportOpen] = useState(false);
  const active = isTranscodeActive(job);
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["transcode-jobs"] });

  const cancel = useMutation({
    mutationFn: () => mediaServer.cancelTranscodeJob(job.id),
    onSuccess: () => {
      setCancelOpen(false);
      return invalidate();
    },
    onError: (error) => toast.error("Couldn’t cancel", { description: errorMessage(error) }),
  });
  const remove = useMutation({
    mutationFn: () => mediaServer.removeTranscodeJob(job.id),
    onSuccess: invalidate,
    onError: (error) => toast.error("Couldn’t dismiss", { description: errorMessage(error) }),
  });

  const downloadExport = useMutation({
    mutationFn: () => mediaServer.downloadTranscodeExport(job.id),
    onSuccess: (url) => window.location.assign(url),
    onError: (error) => toast.error(errorMessage(error)),
  });
  const deleteExport = useMutation({
    mutationFn: () => mediaServer.deleteTranscodeExport(job.id),
    onSuccess: invalidate,
    onError: error => toast.error("Couldn’t delete export", { description: errorMessage(error) }),
  });
  const extraction = job.kind === "Extract";
  const joining = job.kind === "Join";
  const title = job.name ?? job.outputPath ?? job.inputPath;
  // The card's meta line, matching the "catalog · added 2m ago" line on an ingest card: what this run
  // produces, and how long the job has existed. `createdAt` is when the job was queued, not when encoding
  // began (a queued job hasn't started at all), so the line says "added" like the ingest card rather than
  // claiming a start time the API doesn't report.
  const age = formatTimeAgo(job.createdAt);
  // A finished job has to explain where its size went: the picture setting alone does not, once audio can
  // be the larger half of what changed. An extraction encodes nothing, so none of that applies — what it
  // produces is a count of files, and the codec/quality columns would only ever read "Remux".
  const meta = (
    job.kind === "Bluray"
      ? ["Create MKV · Blu-ray", age && `added ${age}`]
      : joining
      ? ["Join parts · 2 files", age && `added ${age}`]
      : extraction
      ? [`Extract · ${job.outputPaths.length} ${job.outputPaths.length === 1 ? "file" : "files"}`, age && `added ${age}`]
      : [
          job.videoCodec === "copy" ? (job.dolbyVision ? "Remux DV 8.1" : "Remux") : job.videoCodec.toUpperCase(),
          job.encoderQuality != null && job.encoderQualityParameter
            ? `${encoderQualityLabel(job.encoderQualityParameter)} ${job.encoderQuality}`
            : job.qualityLevel !== "high" ? job.qualityLevel : null,
          job.reEncodedAudioTracks > 0 ? `${job.reEncodedAudioTracks} audio re-encoded` : null,
          age && `added ${age}`,
        ]
  )
    .filter(Boolean)
    .join(" · ");

  return (
    <ActivityCard>
      <ActivityCardHeader
        title={title}
        titleAttr={title}
        meta={job.outputMountLabel ? meta + " · Export to " + job.outputMountLabel : meta}
        // Waiting for an encoder slot — the queued line replaces the bar and stats below, like a queued move.
        note={job.state === "Queued" ? <ActivityQueued /> : undefined}
        actions={<>{showManagementLink && <ManagementJobLink id={job.mediaItemId} sourceId={job.mediaSourceId} />}{
          active ? (
            <IconAction label="Cancel" icon={<X />} pending={cancel.isPending} onClick={() => setCancelOpen(true)} />
          ) : (
            <IconAction label="Dismiss" icon={<Trash2 />} destructive pending={remove.isPending} onClick={() => remove.mutate()} />
          )
        }</>}
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
      {job.outputMountLabel && !active && <div className="flex items-center gap-3 text-sm">
        {job.state === "Completed" && <Button variant="link" size="sm" disabled={downloadExport.isPending} onClick={() => downloadExport.mutate()}>Download export</Button>}
        <Button size="sm" variant="outline" onClick={() => setDeleteExportOpen(true)}>Delete export</Button>
      </div>}
      <AlertDialog open={deleteExportOpen} onOpenChange={setDeleteExportOpen}>
        <AlertDialogContent>
          <AlertDialogHeader><AlertDialogTitle>Delete this export?</AlertDialogTitle>
            <AlertDialogDescription>Removes the exported file and its job. The original library file stays unchanged.</AlertDialogDescription></AlertDialogHeader>
          <AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction disabled={deleteExport.isPending} onClick={() => deleteExport.mutate()}>Delete export</AlertDialogAction></AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
      <AlertDialog open={cancelOpen && active} onOpenChange={setCancelOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Cancel this conversion?</AlertDialogTitle>
            <AlertDialogDescription>
              Stop <span className="text-foreground font-medium break-words">{title}</span>?
              {" "}You will need to start a new job to try again.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel size="sm" disabled={cancel.isPending}>Keep running</AlertDialogCancel>
            <AlertDialogAction size="sm" variant="destructive" disabled={cancel.isPending || !active} onClick={() => cancel.mutate()}>
              {cancel.isPending ? "Cancelling…" : "Cancel conversion"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </ActivityCard>
  );
}

function stateLabel(job: TranscodeJob): string {
  if (job.state === "Queued") return "Queued";
  if (job.state === "Completed") return job.outputSizeBytes != null ? `Done · ${formatBytes(job.outputSizeBytes)}` : "Done";
  return job.state; // Failed / Canceled — the running case is rendered inline as split stat spans.
}
