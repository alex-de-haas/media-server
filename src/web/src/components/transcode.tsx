"use client";

import { useId, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Info, Trash2, X } from "lucide-react";
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

// Not CRF numbers: the engine maps a level onto whichever encoder the host reaches, so the same level means
// the same picture whether it lands on libx265 or a GPU.
const QUALITY = [
  { value: "highest", label: "Highest — near-transparent" },
  { value: "high", label: "High — recommended" },
  { value: "balanced", label: "Balanced — noticeably smaller" },
  { value: "small", label: "Small — size first" },
];

// What each level came out at on the file the engine's mapping was measured against — a 67.2 Mbps 4K HDR
// remux, where high landed at 19.85 Mbps (the table is in the engine's compression-controls doc). Applied
// as a share of this source's own video bitrate rather than quoted as one absolute figure, so the estimate
// tracks the file in front of the operator. It is an anchor, not a prediction: a level asks for a picture,
// and a source that is already an efficient encode has far less left to give up.
const QUALITY_SHARE: Record<string, number> = {
  highest: 0.44,
  high: 0.3,
  balanced: 0.18,
  small: 0.1,
};

/** E-AC-3 bitrate for a track, by channel count. Sent explicitly so the row can state the resulting size;
 * left to the encoder these would be 448/192/96, which is thriftier than a library wants for 5.1. */
function audioBitrateFor(channels: number | null): number {
  if ((channels ?? 2) > 2) return 640;
  return channels === 1 ? 128 : 256;
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
  return `${before ? `${formatBytes(before)} ` : ""}→ E-AC-3, ${layout}, ${kbps} kbps · about ${size}`;
}

function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? "" : "s"}`;
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
  // 100-ns ticks. Every size on this dialog is a bitrate times this, so they are all comparable with each
  // other — and all absent together on a source whose tracks never recorded one.
  const durationSeconds = source.durationTicks / 10_000_000;
  // The picture, not the poster: a container with embedded cover art carries that as a video stream too, so
  // the costliest one is the one an encode is actually about.
  const videoBytes = source.streams
    .filter((stream) => stream.type === "Video" && !stream.isExternal)
    .reduce<number | null>((largest, stream) => {
      const bytes = streamBytes(stream, durationSeconds);
      return bytes !== null && (largest === null || bytes > largest) ? bytes : largest;
    }, null);
  // Only the tracks that state a bitrate. Leaving the silent ones out can only understate this side, which
  // is the safe direction: the split below claims audio is the larger half, and it must never make that
  // claim on a total it filled in itself.
  const sizedAudio = audioStreams.filter((stream) => stream.bitrate);
  const audioBytes = sizedAudio.reduce((total, stream) => total + (streamBytes(stream, durationSeconds) ?? 0), 0);
  const audioOutweighsVideo = videoBytes !== null && audioBytes > videoBytes;
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
  const qualityId = useId();

  // Opening from Merge means the operator already said "fold these in", and folding in is a copy unless
  // they ask for more — so the dialog opens on "keep the video" rather than silently proposing a re-encode
  // of a file they only wanted more tracks in.
  const initialMode = preselectedSidecars?.length ? "copy" : "encode";
  const [mode, setMode] = useState(initialMode);
  const [codec, setCodec] = useState("hevc");
  const [hardware, setHardware] = useState("auto");
  const [resolution, setResolution] = useState("source");
  const [quality, setQuality] = useState("high");
  // Which kept audio tracks are re-encoded instead of copied, keyed by stream id. Empty is the old
  // behaviour — every track copied byte for byte.
  const [audioReEncoded, setAudioReEncoded] = useState<Set<string>>(() => new Set());
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
      setQuality("high");
      setAudioReEncoded(new Set());
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

  // Whether a track ends up in the output, and can therefore carry an edit at all.
  const isKept = (stream: MediaStream) =>
    stream.isExternal
      ? merged.has(stream.id)
      : stream.type === "Audio"
        ? audioKept.has(stream.index)
        : subtitleKept.has(stream.index);

  // A language the API will refuse, flagged before the whole form is submitted rather than after.
  //
  // The comparison mirrors the service's own normalization: a BCP-47 region or script subtag is dropped
  // ("pt-BR" is Portuguese), and the served list carries the spellings that fold onto a canonical tag, so
  // "ru" and "deu" pass here exactly as they do there. Being stricter than the API is the one direction this
  // check must never be wrong in — it would block a submit the server would have accepted.
  //
  // An empty list (the fetch failed, or has not landed) judges nothing.
  const isBadLanguage = (value: string) => {
    const primary = value.trim().split(/[-_]/)[0].toLowerCase();
    return primary !== "" && !!knownLanguages?.length && !knownLanguages.includes(primary);
  };

  // Only tracks that are actually kept: typing a bad tag and then dropping the track has to unblock the
  // submit, because that value is no longer going anywhere — the edit is not even built for it below.
  const badLanguages = Object.entries(languages).filter(([id, value]) => {
    const stream = source.streams.find((candidate) => candidate.id === id);
    return stream !== undefined && isKept(stream) && isBadLanguage(value);
  });

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
            // A cleared language is "keep", not "erase". The job carries overrides, and there is no override
            // that removes a tag — sending "" would just be refused as not a language, failing the whole
            // submit over a field the operator emptied rather than filled.
            language:
              language !== undefined && language !== "" && language !== (stream?.language ?? "")
                ? language
                : undefined,
          };
        })
        .filter((edit) => edit.title !== undefined || edit.language !== undefined);

      const input: CreateTranscodeInput = {
        sourceId: source.id,
        videoCodec: isCopy ? "copy" : codec,
        hardwareAcceleration: isCopy ? undefined : hardware,
        // A level is meaningless without an encode, and the API refuses it alongside a copied video.
        qualityLevel: isCopy ? null : quality,
        maxHeight: !isCopy && resolution !== "source" ? Number(resolution) : null,
        audioStreamIndexes: audioChanged ? keptAudio : undefined,
        subtitleStreamIndexes: subtitlesChanged ? keptSubtitles : undefined,
        defaultAudioStreamIndex: audioDefaultChanged ? defaultAudio : undefined,
        defaultSubtitleStreamIndex: subtitleDefaultChanged ? defaultSubtitle : undefined,
        mergeStreamIds: merging ? mergeStreamIds : undefined,
        metadataEdits,
        // Only tracks that survive: re-encoding one the job drops is contradictory, and the API refuses it.
        audioTargets: audioStreams
          .filter((stream) => audioReEncoded.has(stream.id) && audioKept.has(stream.index))
          .map((stream) => ({ streamId: stream.id, codec: "eac3", bitrate: audioBitrateFor(stream.channels) })),
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

          {/* Before the video controls on purpose. Re-encoding the picture is the expensive, lossy, day-long
              option and it is the one this dialog opens on; when the dubs are the larger half, the operator
              should learn that before scrolling past it. */}
          {audioOutweighsVideo ? (
            <div className="text-muted-foreground flex items-start gap-2 rounded-md border p-2 text-xs">
              <Info className="mt-0.5 size-3.5 shrink-0" />
              <span>
                Audio is the larger half of this file: {formatBytes(audioBytes)} across{" "}
                {plural(sizedAudio.length, "track")}, against {formatBytes(videoBytes ?? 0)} of video. Dropping
                tracks and re-encoding the ones you keep is the bigger win, and it leaves the picture untouched.
              </span>
            </div>
          ) : null}

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

              <Field>
                <FieldLabel htmlFor={qualityId}>Quality</FieldLabel>
                <Select value={quality} onValueChange={(value) => setQuality((value as string | null) ?? "high")} items={QUALITY}>
                  <SelectTrigger id={qualityId} className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {QUALITY.map((option) => (
                      <SelectItem key={option.value} value={option.value}>
                        {option.label}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {/* "Level 3" means nothing to someone deciding whether to spend a day of CPU on a file;
                    what it costs does. Absent — rather than guessed at — when the source never recorded a
                    video bitrate, which is the only figure this can honestly come from. */}
                {videoBytes !== null ? (
                  <p className="text-xs">
                    About {formatBytes(videoBytes * QUALITY_SHARE[quality])} of video
                    {resolution !== "source" ? ` or less at ${resolution}p` : ""}, from {formatBytes(videoBytes)}.
                  </p>
                ) : null}
                <p className="text-muted-foreground text-xs">
                  The same level on every encoder — the engine translates it for whichever one runs the job.
                  {videoBytes !== null
                    ? " The size is an anchor rather than a promise: a level asks for a picture, and what that costs follows the content."
                    : ""}
                </p>
              </Field>
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
            isBadLanguage={isBadLanguage}
            isReEncoded={(stream) => audioReEncoded.has(stream.id)}
            onReEncode={(stream, checked) =>
              setAudioReEncoded((current) => {
                const next = new Set(current);
                if (checked) next.add(stream.id);
                else next.delete(stream.id);
                return next;
              })
            }
            reEncodeHint={(stream) => audioReEncodeHint(stream, durationSeconds)}
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
            isBadLanguage={isBadLanguage}
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
            isBadLanguage={isBadLanguage}
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
  isBadLanguage,
  isReEncoded,
  onReEncode,
  reEncodeHint,
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
  /** The submit gate's own rule, passed down so a row cannot flag by a different one than it blocks by. */
  isBadLanguage: (value: string) => boolean;
  /** Audio only: re-encode this track instead of copying it. Absent for the lists where it has no meaning. */
  isReEncoded?: (stream: MediaStream) => boolean;
  onReEncode?: (stream: MediaStream, checked: boolean) => void;
  /** What the operator gets for it — the resulting codec, layout and size. */
  reEncodeHint?: (stream: MediaStream) => string;
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
          // Only while the track is kept: a dropped track carries no edit, so flagging its field would point
          // at something that is not blocking anything.
          const badLanguage = checked && isBadLanguage(language);
          return (
            <li key={stream.id} className="flex flex-col gap-1">
              <div className="flex items-center gap-2">
                <Checkbox checked={checked} onCheckedChange={(value) => onToggle(stream, value === true)} aria-label={`Copy ${label}`} />
                <span className="min-w-0 flex-1 truncate leading-6">{label}</span>
                {stream.fileName ? (
                  <span className="text-muted-foreground hidden shrink-0 font-mono text-xs sm:inline">{stream.fileName}</span>
                ) : null}
                {onReEncode ? (
                  <Button
                    type="button"
                    size="sm"
                    variant={isReEncoded?.(stream) ? "secondary" : "ghost"}
                    className="h-6 shrink-0 px-2 text-xs"
                    disabled={!checked}
                    aria-pressed={isReEncoded?.(stream) ?? false}
                    onClick={() => onReEncode(stream, !(isReEncoded?.(stream) ?? false))}
                  >
                    Re-encode
                  </Button>
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
              {/* Stated on the row rather than once for the dialog, because the downmix is a real loss and
                  it is this track that takes it. */}
              {checked && isReEncoded?.(stream) && reEncodeHint ? (
                <p className="text-muted-foreground pl-6 text-xs">{reEncodeHint(stream)}</p>
              ) : null}
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
  // A finished job has to explain where its size went: the picture setting alone does not, once audio can
  // be the larger half of what changed.
  const meta = [
    job.videoCodec === "copy" ? "Remux" : job.videoCodec.toUpperCase(),
    job.qualityLevel && job.qualityLevel !== "high" ? job.qualityLevel : null,
    job.reEncodedAudioTracks > 0
      ? `${job.reEncodedAudioTracks} audio re-encoded`
      : null,
    age && `added ${age}`,
  ]
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
