"use client";

import { IndexingIndicator } from "@/components/indexing-status";
import { useEffect, useId, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AudioLines, Captions, Check, ChevronDown, FileOutput, FileQuestion, Film, Pencil, Shrink, Star, Trash2, type LucideIcon } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type LibraryMediaSource, type MediaStream, type TranscodeJob } from "@/lib/media-server";
import { JoinPartsControl } from "@/components/join-parts-dialog";
import { ExtractDialog } from "@/components/extract-dialog";
import { TranscodeDialog, TranscodeJobRow, isTranscodeActive } from "@/components/transcode";
import { dolbyVisionNote, dynamicRangeBadges, formatBytes, formatRuntime, pictureStream, versionStem } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
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
import { Badge } from "@/components/ui/badge";
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
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { cn } from "@/lib/utils";
import { useSession } from "@/components/app-shell";

/** The movie or episode a set of versions belongs to: what a job attaches to, and what a refetch targets. */
export interface MediaOwner {
  id: string;
  kind: string;
  title: string;
}

/**
 * A title's media: its versions, the tracks inside them, the sidecars beside them, and — for an admin — every
 * control that changes them. One component for a movie's Media tab and an episode's expanded row, so the two
 * cannot drift: the API accepts the same calls for either kind, and this is the one place that offers them.
 *
 * `moving` locks the controls while a move is relocating these very files (an episode's move is its
 * series'); the API rejects those calls with a 409 anyway, so they are not offered. `onChanged` runs after
 * anything here changes a version, beyond the owner's own detail query — an episode row uses it to refresh
 * the season's summary lines.
 */
export function MediaSources({
  owner,
  sources,
  defaultSourceId,
  moving,
  showConversions = true,
  onChanged,
}: {
  owner: MediaOwner;
  sources: LibraryMediaSource[];
  defaultSourceId: string | null;
  moving: boolean;
  /** The Conversions block above the versions — a movie's own. A series lists its episodes' jobs in one place. */
  showConversions?: boolean;
  onChanged?: () => void;
}) {
  const { role } = useSession();
  const queryClient = useQueryClient();
  const admin = role === "admin" && (owner.kind === "Movie" || owner.kind === "Episode");
  const canManage = admin && !moving;
  const changed = () => {
    queryClient.invalidateQueries({ queryKey: ["library-detail", owner.id] });
    onChanged?.();
  };

  if (!sources.length && !canManage) {
    return <EmptyMediaPanel>No media sources available.</EmptyMediaPanel>;
  }

  return (
    <section className="flex flex-col gap-3">
      {admin && showConversions && <Conversions itemIds={[owner.id]} onSettled={changed} />}
      {canManage && owner.kind === "Movie" && sources.length > 1 && <JoinPartsControl sources={sources} itemId={owner.id} />}
      {sources.length ? (
        sources.map((source) => (
          <SourceCard
            key={source.id}
            source={source}
            itemId={owner.id}
            canManage={canManage}
            isDefault={source.id === defaultSourceId}
            hasMultiple={sources.length > 1}
            onChanged={changed}
          />
        ))
      ) : (
        <EmptyMediaPanel>No media sources available.</EmptyMediaPanel>
      )}
    </section>
  );
}

function EmptyMediaPanel({ children }: { children: string }) {
  return <p className="text-muted-foreground py-6 text-sm">{children}</p>;
}

// Stream types we know how to order/label; anything else falls through in its original order with its raw
// type name. "Subtitle" reads better pluralised once it heads a group of them.
const STREAM_TYPE_ORDER = ["Video", "Audio", "Subtitle"];
const STREAM_TYPE_LABELS: Record<string, string> = { Subtitle: "Subtitles" };

type StreamGroup = { type: string; label: string; streams: MediaStream[]; defaultIndex: number | null };

// Group a stream list by type, preserving each individual track (no dedup) and container order. Types we
// don't know sort last under their raw name.
function groupByType(streams: MediaStream[]): { type: string; label: string; streams: MediaStream[] }[] {
  const byType = new Map<string, MediaStream[]>();
  for (const stream of streams) {
    const list = byType.get(stream.type) ?? [];
    list.push(stream);
    byType.set(stream.type, list);
  }

  const rank = (type: string) => {
    const index = STREAM_TYPE_ORDER.indexOf(type);
    return index === -1 ? STREAM_TYPE_ORDER.length : index;
  };

  return [...byType.keys()]
    .sort((a, b) => rank(a) - rank(b))
    .map((type) => ({ type, label: STREAM_TYPE_LABELS[type] ?? type, streams: byType.get(type)! }));
}

// The container's own tracks. `defaultIndex` is the track a player treats as default — an explicitly
// flagged track, or (audio only) the first track when none is flagged; subtitles stay off unless flagged.
// Matches the Convert dialog. External tracks are sidecar files beside the video, not tracks inside it:
// they are listed separately, with their own actions, so they are left out here.
function groupStreams(streams: MediaStream[]): StreamGroup[] {
  return groupByType(streams.filter((stream) => !stream.isExternal)).map((group) => {
    const flagged = group.streams.find((stream) => stream.isDefault)?.index;
    const defaultIndex = flagged ?? (group.type === "Audio" ? group.streams[0]?.index : undefined) ?? null;
    return { ...group, defaultIndex };
  });
}

// Secondary technical specs shown muted after a track: video → profile · bit depth · frame rate; audio →
// profile · sample rate. Only what the probe captured; "" when nothing to add. Numbers are trimmed of
// trailing zeros.
//
// Audio carries its profile for one fact in particular: Atmos and DTS:X live there and nowhere else — the
// codec reads `truehd` either way — and they are what decides whether a track may be re-encoded at all.
function streamSpecs(stream: MediaStream): string {
  const parts: string[] = [];
  if (stream.type === "Video") {
    if (stream.profile) parts.push(stream.profile);
    if (stream.bitDepth) parts.push(`${stream.bitDepth}-bit`);
    if (stream.frameRate) parts.push(`${Number(stream.frameRate.toFixed(3))} fps`);
  } else if (stream.type === "Audio") {
    if (stream.profile) parts.push(stream.profile);
    if (stream.sampleRate) parts.push(`${Number((stream.sampleRate / 1000).toFixed(1))} kHz`);
  }
  return parts.join(" · ");
}

// The per-track summary text, its optional container label ("Director's Commentary", "SDH") — dropping a
// label that just restates the summary — and the muted secondary specs.
//
// `titleLeads` is for sidecars, which routinely have neither language nor codec: an `.ac3` dub named only
// after its voice-over group has nothing else to head the row, and `— "Гаврилов"` says less than
// `Гаврилов` does. When the label leads, it stops being repeated as a trailing quote on its own.
function TrackText({ stream, titleLeads = false }: { stream: MediaStream; titleLeads?: boolean }) {
  const raw = stream.title?.trim() || null;
  const text = stream.displayTitle ?? stream.codec ?? (titleLeads ? raw : null) ?? "—";
  const title = raw && raw.toLowerCase() !== text.toLowerCase() ? raw : null;
  const specs = streamSpecs(stream);
  return (
    <>
      {text}
      {title ? <span className="text-muted-foreground"> “{title}”</span> : null}
      {specs ? <span className="text-muted-foreground"> · {specs}</span> : null}
    </>
  );
}

// What each kind of track is, at a glance — the same mark on the container's own tracks and on the sidecars
// below them, so the two lists read as one vocabulary. A type the probe reports that isn't one of these gets
// the fallback rather than a guess.
const STREAM_ICONS: Record<string, LucideIcon> = { Video: Film, Audio: AudioLines, Subtitle: Captions };

// The type's icon and name, as the left column of a track row. Wide enough for "Subtitles" beside an icon.
function StreamTypeLabel({ type, label }: { type: string; label: string }) {
  const Icon = STREAM_ICONS[type] ?? FileQuestion;
  return (
    <dt className="text-muted-foreground flex w-24 shrink-0 items-center gap-1.5 pt-px text-xs leading-5">
      <Icon className="size-3.5 shrink-0" />
      <span className="truncate">{label}</span>
    </dt>
  );
}

// One "Video/Audio/Subtitles" section. A single track shows inline; a section with several collapses into a
// toggle ("N tracks") that expands to list every track, marking the default one.
function StreamSection({ group }: { group: StreamGroup }) {
  const [open, setOpen] = useState(false);

  if (group.streams.length <= 1) {
    const stream = group.streams[0];
    return (
      <div className="flex gap-2">
        <StreamTypeLabel type={group.type} label={group.label} />
        <dd className="leading-5">{stream ? <TrackText stream={stream} /> : "—"}</dd>
      </div>
    );
  }

  return (
    <div className="flex gap-2">
      <StreamTypeLabel type={group.type} label={group.label} />
      <dd className="min-w-0 flex-1">
        <button
          type="button"
          aria-expanded={open}
          onClick={() => setOpen((value) => !value)}
          className="text-muted-foreground hover:text-foreground flex items-center gap-1 leading-5"
        >
          <ChevronDown className={cn("size-3.5 transition-transform", open ? "" : "-rotate-90")} />
          <span>{group.streams.length} tracks</span>
        </button>
        {open ? (
          <div className="mt-0.5 flex flex-col gap-0.5">
            {group.streams.map((stream) => (
              <span key={stream.index} className="flex items-center gap-1.5 leading-5">
                <span>
                  <TrackText stream={stream} />
                </span>
                {stream.index === group.defaultIndex ? (
                  <Check className="text-primary size-3.5 shrink-0" aria-label="Default track" />
                ) : null}
              </span>
            ))}
          </div>
        ) : null}
      </dd>
    </div>
  );
}

// One source/version card. The file name's stem is read-only; the editable label is the version (shown in
// players' version pickers) and an admin can pin which version plays by default, convert it into a smaller
// version, extract its tracks, or delete it (the "verify then replace" flow: convert → check the new
// version → delete the original). The same card for a movie's version and an episode's.
function SourceCard({
  source,
  itemId,
  canManage,
  isDefault,
  hasMultiple,
  onChanged,
}: {
  source: LibraryMediaSource;
  itemId: string;
  canManage: boolean;
  isDefault: boolean;
  hasMultiple: boolean;
  onChanged: () => void;
}) {
  // The engine is an optional dependency. Everything else on this tab — the version list, renaming, the
  // default-version pick — is database-side and works without it, so only the convert control keys off this.
  const { data: transcode } = useQuery({
    queryKey: ["transcode-availability"],
    queryFn: () => mediaServer.transcodeAvailability(),
    staleTime: 5 * 60 * 1000,
    // The endpoint is admin-only, and only an admin sees the controls it gates — asking as a plain viewer
    // would just be a 401 on every version card.
    enabled: canManage,
  });
  const canConvert = canManage && (transcode?.available ?? false);
  const [convertOpen, setConvertOpen] = useState(false);
  const [extractOpen, setExtractOpen] = useState(false);
  // Sidecars the Convert dialog opens with already checked — set when it is reached through Merge below.
  const [preselectedSidecars, setPreselectedSidecars] = useState<string[]>([]);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [editOpen, setEditOpen] = useState(false);
  // The picture — the film, not a cover a muxer wrote as a video track. Its badges carry the Dolby Vision
  // profile when it is recorded, and a dual-layer profile 7 gets the one note a viewer with Apple hardware needs.
  const picture = pictureStream(source.streams);
  const rangeBadges = dynamicRangeBadges(picture?.hdrFormat, picture?.dolbyVision);
  const rangeNote = dolbyVisionNote(picture?.dolbyVision);

  // The default only matters when a title has several versions; clients play MediaSources[0].
  const showDefault = hasMultiple;

  // Header meta: container · size · duration · overall bitrate. The container often omits an overall
  // bitrate (typical for MKV), so fall back to the average derived from size ÷ duration — display only.
  const bitrateKbps =
    source.bitrate != null && source.bitrate > 0
      ? Math.round(source.bitrate / 1000)
      : source.durationTicks > 0
        ? Math.round((source.sizeBytes * 8) / (source.durationTicks / 1e7) / 1000)
        : null;
  const metaParts = [
    source.container,
    formatBytes(source.sizeBytes),
    formatRuntime(source.durationTicks),
    bitrateKbps ? `${bitrateKbps.toLocaleString()} kbps` : null,
  ].filter(Boolean);

  const setDefault = useMutation({
    mutationFn: (next: boolean) => mediaServer.setDefaultSource(itemId, next ? source.id : null),
    onSuccess: () => {
      onChanged();
      toast.success(isDefault ? "Default version cleared" : "Set as default version");
    },
    onError: (error) => toast.error("Couldn’t update default version", { description: errorMessage(error) }),
  });

  return (
    <div className="rounded-md border p-3 text-sm">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <p className="font-mono font-medium break-all">{source.fileName}</p>
            {rangeBadges.map((badge) => (
              <Badge key={badge} variant="secondary" className="font-normal">
                {badge}
              </Badge>
            ))}
            {rangeNote ? <span className="text-muted-foreground text-xs">{rangeNote}</span> : null}
          </div>
          <p className="text-muted-foreground mt-1 font-mono text-xs">{metaParts.join(" · ")}</p>
          <IndexingIndicator id={source.id} snapshot={source.indexing} />
        </div>
        {canManage && (
          <div className="flex shrink-0 items-center gap-1">
            {showDefault && (
              <Button
                variant="ghost"
                size="icon-sm"
                aria-label={isDefault ? "Clear default version" : "Set as default version"}
                disabled={setDefault.isPending}
                onClick={() => setDefault.mutate(!isDefault)}
              >
                <Star className={cn(isDefault && "fill-current text-amber-500")} />
              </Button>
            )}
            <Button variant="ghost" size="icon-sm" aria-label="Rename version" onClick={() => setEditOpen(true)}>
              <Pencil />
            </Button>
            {canConvert && (
              <Button variant="ghost" size="icon-sm" aria-label="Convert to a smaller version" onClick={() => setConvertOpen(true)}>
                <Shrink />
              </Button>
            )}
            {canConvert && (
              <Button
                variant="ghost"
                size="icon-sm"
                aria-label="Extract tracks to files"
                onClick={() => setExtractOpen(true)}
              >
                <FileOutput />
              </Button>
            )}
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label="Delete this version"
              className="text-destructive hover:text-destructive hover:bg-destructive/10"
              onClick={() => setDeleteOpen(true)}
            >
              <Trash2 />
            </Button>
          </div>
        )}
      </div>
      <dl className="mt-2 flex flex-col gap-1.5">
        {groupStreams(source.streams).map((group) => (
          <StreamSection key={group.type} group={group} />
        ))}
      </dl>

      <SidecarSection
        sidecars={source.streams.filter((stream) => stream.isExternal)}
        onChanged={onChanged}
        canManage={canManage}
        canMerge={canConvert}
        onMerge={(streamIds) => {
          setPreselectedSidecars(streamIds);
          setConvertOpen(true);
        }}
      />

      {canManage && <EditVersionDialog source={source} open={editOpen} onOpenChange={setEditOpen} onChanged={onChanged} />}
      {canManage && canConvert && (
        <TranscodeDialog
          source={source}
          open={convertOpen}
          onOpenChange={(next) => {
            setConvertOpen(next);
            // A later Convert click must not reopen with the last merge's selection still checked.
            if (!next) setPreselectedSidecars([]);
          }}
          preselectedSidecars={preselectedSidecars}
        />
      )}
      {canManage && canConvert && (
        <ExtractDialog source={source} itemId={itemId} open={extractOpen} onOpenChange={setExtractOpen} />
      )}
      {canManage && <DeleteVersionDialog source={source} open={deleteOpen} onOpenChange={setDeleteOpen} onChanged={onChanged} />}
    </div>
  );
}

// Characters the server rejects (they'd be stripped from a filename). Mirrored here so the field flags them
// before the request, but the server stays the source of truth.
const INVALID_VERSION_CHARS = /[/\\:*?"<>|]/;

// Rename or clear a version — the ` - {version}` suffix on its filename, which also labels the version in
// players (e.g. Infuse). This renames the file on disk; the stem before the suffix is locked — a movie's
// `Title (Year)`, an episode's `Show S01E03`.
function EditVersionDialog({
  source,
  open,
  onOpenChange,
  onChanged,
}: {
  source: LibraryMediaSource;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onChanged: () => void;
}) {
  const inputId = useId();
  const [value, setValue] = useState(source.versionName ?? "");

  // Re-seed the field with the current version each time the dialog (re)opens.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) setValue(source.versionName ?? "");
  }

  // Preview the resulting file name: the locked stem + the typed suffix + the current extension. Normalize
  // the suffix the same way the server does (collapse runs of spaces, drop trailing dots) so the preview
  // matches what actually lands on disk. The stem is read off the file name rather than composed from a
  // title and year, which is what makes it right for an episode too; the server rebuilds the real one.
  const trimmed = value.trim();
  const suffix = trimmed.replace(/\s+/g, " ").replace(/\.+$/, "");
  const invalid = INVALID_VERSION_CHARS.test(value);
  const { stem, extension } = versionStem(source.fileName, source.versionName);
  const previewName = `${stem}${suffix ? ` - ${suffix}` : ""}${extension}`;

  const save = useMutation({
    mutationFn: (next: string | null) => mediaServer.setSourceVersion(source.id, next),
    onSuccess: () => {
      onChanged();
      onOpenChange(false);
      toast.success("Version renamed");
    },
    onError: (error) => toast.error("Couldn’t rename version", { description: errorMessage(error) }),
  });

  const submit = () => {
    if (save.isPending || invalid) return; // Guard against double-submit (e.g. repeated Enter) and bad input.
    save.mutate(trimmed ? trimmed : null);
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Rename version</DialogTitle>
          <DialogDescription>
            Renames the file on disk and the label shown in players (e.g. Infuse). The “{stem}” part is locked —
            only the part after it changes.
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-2">
          <Label htmlFor={inputId}>Version</Label>
          <Input
            id={inputId}
            value={value}
            aria-invalid={invalid}
            placeholder="e.g. Remux 1080p, Director’s Cut"
            onChange={(event) => setValue(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter") {
                event.preventDefault();
                submit();
              }
            }}
          />
          {invalid ? (
            <p className="text-destructive text-xs">{`Can’t contain / \\ : * ? " < > |`}</p>
          ) : (
            <p className="text-muted-foreground font-mono text-xs break-all">{previewName}</p>
          )}
        </div>

        <DialogFooter className="gap-2 sm:gap-2">
          {source.versionName ? (
            <Button
              variant="ghost"
              size="sm"
              className="text-destructive hover:text-destructive mr-auto"
              disabled={save.isPending}
              onClick={() => save.mutate(null)}
            >
              Remove version
            </Button>
          ) : null}
          <Button variant="outline" size="sm" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button size="sm" disabled={save.isPending || invalid} onClick={submit}>
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// Sidecar tracks: dub and subtitle files sitting beside the video rather than inside it. They are listed
// apart from the container's own tracks because they behave differently — each is a file that can be
// removed on its own, and a player will not use an external *audio* track at all until it is merged in.
function SidecarSection({
  sidecars,
  onChanged,
  canManage,
  canMerge,
  onMerge,
}: {
  sidecars: MediaStream[];
  onChanged: () => void;
  canManage: boolean;
  canMerge: boolean;
  /** Hands the checked sidecars to the Convert dialog. Merging used to submit a job straight from here, so
   *  there was no way to see what the result would carry, or fix a track's name, before it started. */
  onMerge: (streamIds: string[]) => void;
}) {
  const [selected, setSelected] = useState<string[]>([]);

  const remove = useMutation({
    mutationFn: ({ id, deleteFile }: { id: string; deleteFile: boolean }) =>
      mediaServer.deleteExternalStream(id, deleteFile),
    onSuccess: () => {
      onChanged();
      toast.success("Track removed");
    },
    onError: (error) => toast.error("Couldn’t remove the track", { description: errorMessage(error) }),
  });

  if (sidecars.length === 0) {
    return null;
  }

  // Grouped by kind, because a dub and a subtitle are not interchangeable and a sidecar row often has
  // nothing on it that would say which is which — no language, no codec, just the label its release gave
  // it. The kind heads the group; the file name under each row is the other half of the answer, and the
  // thing an operator sees when they open the folder.
  const groups = groupByType(sidecars);

  return (
    <div className="mt-2 border-t pt-2">
      <p className="text-muted-foreground text-xs">
        {sidecars.length} separate {sidecars.length === 1 ? "file" : "files"} beside this version
      </p>
      {groups.map((group) => {
        const Icon = STREAM_ICONS[group.type] ?? FileQuestion;
        return (
          // The gap before a heading has to beat the gap inside a group, or the last row of one kind reads
          // as belonging to the next.
          <div key={group.type} className="mt-3">
            <p className="text-muted-foreground flex items-center gap-1.5 text-xs">
              <Icon className="size-3.5 shrink-0" />
              <span className="font-medium">{group.label}</span>
              {group.type === "Audio" && (
                // Worth stating plainly, and right where the dubs are: keeping one as a file preserves it,
                // but no player will use it there.
                <span>— only plays once merged into the video</span>
              )}
            </p>
            <ul className="mt-1 flex flex-col gap-1">
              {group.streams.map((stream) => (
                <li key={stream.id} className="flex items-start gap-2 text-sm">
                  {canManage && canMerge && (
                    <Checkbox
                      className="mt-1"
                      checked={selected.includes(stream.id)}
                      onCheckedChange={(checked) =>
                        setSelected((current) =>
                          checked ? [...current, stream.id] : current.filter((id) => id !== stream.id),
                        )
                      }
                      aria-label={`Merge ${stream.fileName ?? stream.displayTitle ?? stream.type} into a new version`}
                    />
                  )}
                  <span className="min-w-0 flex-1">
                    <span className="block truncate leading-6">
                      <TrackText stream={stream} titleLeads />
                    </span>
                    <IndexingIndicator id={stream.id} snapshot={stream.indexing} />
                    {stream.fileName && (
                      <span className="text-muted-foreground block truncate font-mono text-xs">
                        {stream.fileName}
                      </span>
                    )}
                  </span>
                  {canManage && (
                    <Button
                      variant="ghost"
                      size="icon-sm"
                      aria-label={`Remove ${stream.fileName ?? "this track"}`}
                      className="text-destructive hover:text-destructive hover:bg-destructive/10"
                      disabled={remove.isPending}
                      onClick={() => remove.mutate({ id: stream.id, deleteFile: true })}
                    >
                      <Trash2 />
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          </div>
        );
      })}
      {canManage && canMerge && selected.length > 0 && (
        <Button
          size="sm"
          variant="outline"
          className="mt-2"
          onClick={() => {
            // Handing the selection to the dialog consumes it. Keeping it checked here would leave two
            // places claiming to hold the answer, and the stale one wins the next time this button is
            // pressed — after the dialog's own selection has moved on.
            onMerge(selected);
            setSelected([]);
          }}
        >
          Merge {selected.length} into a new version…
        </Button>
      )}
    </div>
  );
}

/**
 * The transcode jobs of these items — a movie's own, or every episode's of a series — polled while any is
 * active. When the last one finishes, `onSettled` runs so the freshly produced version appears where the
 * versions are listed. Admin-only, like the endpoint it reads.
 */
export function Conversions({ itemIds, onSettled }: { itemIds: string[]; onSettled: () => void }) {
  const owned = new Set(itemIds);
  const jobs = useQuery({
    queryKey: ["transcode-jobs"],
    queryFn: mediaServer.listTranscodeJobs,
    refetchInterval: (query) => {
      const data = (query.state.data ?? []) as TranscodeJob[];
      return data.some((job) => owned.has(job.mediaItemId) && isTranscodeActive(job)) ? 2000 : false;
    },
  });

  const mine = (jobs.data ?? []).filter((job) => owned.has(job.mediaItemId));
  const activeCount = mine.filter(isTranscodeActive).length;

  const previousActive = useRef(activeCount);
  useEffect(() => {
    if (previousActive.current > 0 && activeCount === 0) {
      onSettled();
    }
    previousActive.current = activeCount;
  }, [activeCount, onSettled]);

  if (mine.length === 0) {
    return null;
  }

  // A label over a stack of conversion cards, mirroring the Activity page's groups — each job already draws
  // its own bordered card, so this block adds no box of its own.
  return (
    <section className="flex flex-col gap-2">
      <p className="text-muted-foreground text-xs font-medium">Conversions</p>
      <div className="flex flex-col gap-3">
        {mine.map((job) => (
          <TranscodeJobRow key={job.id} job={job} />
        ))}
      </div>
    </section>
  );
}

function DeleteVersionDialog({
  source,
  open,
  onOpenChange,
  onChanged,
}: {
  source: LibraryMediaSource;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onChanged: () => void;
}) {
  const deleteFileId = useId();
  const [deleteFile, setDeleteFile] = useState(false);

  // Re-apply the default each time the dialog (re)opens so a prior toggle (then cancel) doesn't carry over.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) setDeleteFile(false);
  }

  const remove = useMutation({
    mutationFn: () => mediaServer.deleteMediaSource(source.id, deleteFile),
    onSuccess: () => {
      onChanged();
      onOpenChange(false);
      toast.success("Version removed");
    },
    onError: (error) => toast.error("Couldn’t remove version", { description: errorMessage(error) }),
  });

  return (
    <AlertDialog open={open} onOpenChange={onOpenChange}>
      <AlertDialogContent className="sm:max-w-md">
        <AlertDialogHeader>
          <AlertDialogTitle>Remove this version?</AlertDialogTitle>
          <AlertDialogDescription>
            Removes <span className="text-foreground font-medium">{source.versionName ?? source.container}</span> (
            {formatBytes(source.sizeBytes)}) from this title.
          </AlertDialogDescription>
        </AlertDialogHeader>

        <div className="flex items-start gap-2 rounded-md border p-3 text-sm">
          <Checkbox
            id={deleteFileId}
            className="mt-0.5"
            checked={deleteFile}
            onCheckedChange={(checked) => setDeleteFile(checked === true)}
          />
          <label htmlFor={deleteFileId} className="cursor-pointer">
            Delete file from disk
            <span className="text-muted-foreground block text-xs">
              Frees the disk space. Otherwise only the library entry is removed.
            </span>
          </label>
        </div>

        <AlertDialogFooter>
          <AlertDialogCancel size="sm">Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" size="sm" disabled={remove.isPending} onClick={() => remove.mutate()}>
            {deleteFile ? "Delete + remove file" : "Remove version"}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
