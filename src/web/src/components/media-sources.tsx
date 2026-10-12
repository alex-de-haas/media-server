"use client";

import { diagnosticReason, type SourceDiagnostic } from "@/lib/management";
import Link from "next/link";
import { IndexingIndicator } from "@/components/indexing-status";
import { ManagementAvailability } from "@/components/management-status";
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, AudioLines, Captions, ChevronDown, Disc3, FileOutput, FileQuestion, Film, Package, PackagePlus, Pencil, Star, Trash2, type LucideIcon } from "lucide-react";
import { toast } from "@/lib/toast";
import { BlurayDialog } from "@/components/bluray-dialog";
import { mediaServer, isBluraySource, type LibraryMediaSource, type MediaStream, type TranscodeJob } from "@/lib/media-server";
import { JoinPartsControl } from "@/components/join-parts-dialog";
import { cleanTrackSelection, compositionReason, extractionReason, toggleTrackSelection, trackSelectionSource, trackLabel, type TrackOperation, type TrackSelection } from "@/lib/track-actions";
import { TranscodeDialog, TranscodeJobRow, isTranscodeActive } from "@/components/transcode";
import { dolbyVisionNote, dynamicRangeBadges, formatBytes, formatRuntime, pictureStream, versionStem } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
import { ApiError } from "@/lib/api";
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
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { useSession } from "@/components/app-shell";

/** The movie or episode a set of versions belongs to: what a job attaches to, and what a refetch targets. */
export interface MediaOwner {
  id: string;
  kind: string;
  title: string;
  runtimeTicks?: number | null;
}

/**
 * A title's media: its versions, the tracks inside them, the sidecars beside them, and — for an admin — every
 * control that changes them. One compact table for movie and episode management, so the two
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
  diagnostics,
  highlightMatches = false,
  location,
  onChanged,
  onDeleteItem,
}: {
  diagnostics?: SourceDiagnostic[];
  highlightMatches?: boolean;
  location?: ReactNode;
  owner: MediaOwner;
  sources: LibraryMediaSource[];
  defaultSourceId: string | null;
  moving: boolean;
  /** The Conversions block above the versions — a movie's own. A series lists its episodes' jobs in one place. */
  showConversions?: boolean;
  onChanged?: () => void;
  /** Opens the existing whole-item dialog when this is the last version. Never deletes immediately. */
  onDeleteItem: () => void;
}) {
  const { role } = useSession();
  const queryClient = useQueryClient();
  const admin = role === "admin" && (owner.kind === "Movie" || owner.kind === "Episode");
  const canManage = admin && !moving;
  const changed = () => {
    queryClient.invalidateQueries({ queryKey: ["library-detail", owner.id] });
    queryClient.invalidateQueries({ queryKey: ["management"] });
    queryClient.invalidateQueries({ queryKey: ["library-availability"] });
    onChanged?.();
  };

  const [selection, setSelection] = useState<TrackSelection | null>(null);
  const cleaned = cleanTrackSelection(selection, sources);
  if (JSON.stringify(cleaned) !== JSON.stringify(selection)) setSelection(cleaned);
  const selectedSource = trackSelectionSource(cleaned?.sourceId, sources);
  const selected = selectedSource?.streams.filter(stream => cleaned?.streamIds.includes(stream.id)) ?? [];
  const hasEmbeddedSelection = selected.some(stream => !stream.isExternal);
  // A refreshed source invalidates an open confirmation if any of its selected facts changed.
  const signature = JSON.stringify([selectedSource?.id, selected]);
  const [operation, setOperation] = useState<{ kind: TrackOperation; signature: string } | null>(null);
  const { data: engine } = useQuery({ queryKey: ["transcode-availability"], queryFn: mediaServer.transcodeAvailability, enabled: canManage, staleTime: 5 * 60 * 1000 });
  const engineReason = !engine?.available ? "Transcode Engine is unavailable." : null;
  const composeReason = engineReason ?? (!engine?.explicitVideoSelection ? "Update Transcode Engine to use Remux and Convert with track selection." : compositionReason(selected));
  const extractReason = engineReason ?? selected.map(extractionReason).find(Boolean) ?? null;
  const extract = useMutation({
    mutationFn: ({ sourceId, streamIds }: TrackSelection) => mediaServer.extractTracks({ sourceId, streamIds }),
    onSuccess: () => {
      setSelection(null);
      queryClient.invalidateQueries({ queryKey: ["transcode-jobs"] });
      changed();
      toast.success("Extraction started");
    },
    onError: error => toast.error("Couldn’t extract tracks", { description: errorMessage(error) }),
  });
  const selectionControl = (source: LibraryMediaSource, stream: MediaStream) => {
    const locked = !stream.isExternal && hasEmbeddedSelection && cleaned?.sourceId !== source.id;
    return canManage && !isBluraySource(source) && (
    <Checkbox checked={cleaned?.streamIds.includes(stream.id) ?? false}
      disabled={extract.isPending || locked}
      aria-label={`Select ${trackLabel(stream)} in ${source.fileName}`}
      title={locked ? "Clear the selected embedded tracks to select another container." : undefined}
      onCheckedChange={checked => { if (!extract.isPending) setSelection(toggleTrackSelection(cleaned, source, stream, checked, sources)); }} />
    );
  };

  return (
    <section className="flex min-w-0 flex-col gap-2">
      {admin && showConversions && <Conversions itemIds={[owner.id]} onSettled={changed} />}
      <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2">
        {location}
        {canManage && owner.kind === "Movie" && sources.filter(s => !isBluraySource(s)).length > 1 && <JoinPartsControl sources={sources.filter(s => !isBluraySource(s))} itemId={owner.id} />}
      </div>
      {sources.length ? (
        <div className="overflow-hidden rounded-md border bg-background">
          <Table className="min-w-[1100px] table-fixed text-xs" aria-label={`Versions of ${owner.title}`}>
            <colgroup>
              <col className="w-[30%]" /><col className="w-[9%]" /><col className="w-[23%]" />
              <col className="w-[8%]" /><col className="w-[16%]" /><col className="w-44" />
            </colgroup>
            <TableHeader className="bg-muted/50">
              <TableRow>
                {["File / track", "Type", "Format / details", "Size / rate", "Status", "Actions"].map(label => (
                  <TableHead key={label} className="h-8" scope="col">{label}</TableHead>
                ))}
              </TableRow>
            </TableHeader>
            {canManage && selectedSource && selected.length > 0 && <TableBody>
              <TableRow className="bg-primary/5 hover:bg-primary/5">
                <TableCell colSpan={6}>
                  <div className="flex flex-wrap items-center gap-2" aria-label="Selected track actions">
                    <span className="mr-auto min-w-0 break-words"><strong>{selected.length} selected</strong><span className="text-muted-foreground"> · {selectedSource.fileName}</span></span>
                    <Button size="sm" variant="outline" disabled={!!extractReason || extract.isPending} title={extractReason ?? undefined} onClick={() => extract.mutate(cleaned!)}><FileOutput />Extract</Button>
                    <Button size="sm" variant="outline" disabled={!!composeReason || extract.isPending} onClick={() => setOperation({ kind: "remux", signature })}>Remux</Button>
                    <Button size="sm" disabled={!!composeReason || extract.isPending} onClick={() => setOperation({ kind: "convert", signature })}>Convert</Button>
                    <Button size="sm" variant="ghost" disabled={extract.isPending} onClick={() => setSelection(null)}>Clear selection</Button>
                  </div>
                  {composeReason && <p className="mt-1 text-muted-foreground" role="status">{composeReason}</p>}
                  {extractReason && extractReason !== engineReason && <p className="mt-1 text-muted-foreground">Extract: {extractReason}</p>}
                </TableCell>
              </TableRow>
            </TableBody>}
            {sources.map((source) => (
              <SourceRow
                key={source.id}
                source={source}
                selectionControl={stream => selectionControl(source, stream)}
                extractTrack={stream => extract.mutate({ sourceId: source.id, streamIds: [stream.id] })}
                extracting={extract.isPending}
                diagnostic={diagnostics?.find(value => value.id === source.id)}
                highlightMatches={highlightMatches}
                itemId={owner.id}
                runtimeTicks={owner.runtimeTicks}
                canManage={canManage}
                isDefault={source.id === defaultSourceId}
                hasMultiple={sources.length > 1}
                onChanged={changed}
                ownerKind={owner.kind}
                onDeleteItem={onDeleteItem}
              />
            ))}
          </Table>
        </div>
      ) : (
        <EmptyMediaPanel>No media sources available.</EmptyMediaPanel>
      )}
      {canManage && selectedSource && operation?.signature === signature && <TranscodeDialog
        source={selectedSource} selected={selected} operation={operation.kind}
        onClose={() => setOperation(null)} onSubmitted={() => { setOperation(null); setSelection(null); }} />}
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
  } else if (stream.type === "Subtitle" && stream.codec && stream.displayTitle && !stream.displayTitle.toLowerCase().includes(stream.codec.toLowerCase())) {
    parts.push(stream.codec.toUpperCase());
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

// Embedded tracks and separate files share columns and the same metadata renderer. Only the origin,
// available actions differ; playback availability belongs to the container, not a track.
function TrackRow({
  stream,
  isDefault = false,
  selection,
  actions,
}: {
  stream: MediaStream;
  isDefault?: boolean;
  selection?: ReactNode;
  actions?: ReactNode;
}) {
  const Icon = STREAM_ICONS[stream.type] ?? FileQuestion;
  const external = stream.isExternal;
  return (
    <TableRow className={cn("[&>td]:whitespace-normal [&>td]:py-1.5", !external && "bg-muted/20")}>
      <TableCell>
        <div className="flex items-center gap-2">
          <span className="flex w-7 shrink-0 justify-center">{selection}</span>
          <Icon className="text-muted-foreground size-4 shrink-0" aria-hidden />
          <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5">
            <span className="break-all">{external ? stream.fileName ?? stream.title ?? "Separate file" : `Track ${stream.index + 1}`}</span>
            {(isDefault || stream.isForced) && (
              <span className="flex flex-wrap items-center gap-2 text-muted-foreground">
                {isDefault && <Badge variant="secondary">Default</Badge>}
                {stream.isForced && <span>Forced</span>}
              </span>
            )}
          </div>
        </div>
      </TableCell>
      <TableCell className="text-muted-foreground">{STREAM_TYPE_LABELS[stream.type] ?? stream.type}</TableCell>
      <TableCell><TrackText stream={stream} titleLeads /></TableCell>
      <TableCell className="text-muted-foreground tabular-nums">
        {stream.bitrate != null && stream.bitrate > 0
          ? stream.bitrate < 1000 ? `${stream.bitrate.toLocaleString()} bps` : `${Math.round(stream.bitrate / 1000).toLocaleString()} kbps`
          : "—"}
      </TableCell>
      <TableCell>
        <div className="flex flex-col gap-0.5">
          <span className="text-muted-foreground">{external ? "Separate file" : "Embedded"}</span>
          {external && stream.type === "Audio" && (
            <span className="text-warning-foreground flex items-center gap-1" title="Only plays once merged into the video">
              <AlertTriangle className="size-3 shrink-0" aria-hidden />Merge for playback
            </span>
          )}
          {external && <IndexingIndicator id={stream.id} snapshot={stream.indexing} />}
        </div>
      </TableCell>
      <TableCell>{actions}</TableCell>
    </TableRow>
  );
}

// One source/version row. The file name's stem is read-only; the editable label is the version (shown in
// players' version pickers) and an admin can pin which version plays by default, convert it into a smaller
// version, extract its tracks, or delete it (the "verify then replace" flow: convert → check the new
// version → delete the original). The same row for a movie's version and an episode's.
function SourceRow({
  source,
  selectionControl, extractTrack, extracting,
  diagnostic,
  highlightMatches,
  itemId,
  runtimeTicks,
  canManage,
  isDefault,
  hasMultiple,
  onChanged,
  ownerKind,
  onDeleteItem,
}: {
  source: LibraryMediaSource;
  selectionControl: (stream: MediaStream) => ReactNode;
  extractTrack: (stream: MediaStream) => void;
  extracting: boolean;
  diagnostic?: SourceDiagnostic;
  highlightMatches: boolean;
  itemId: string;
  runtimeTicks?: number | null;
  canManage: boolean;
  isDefault: boolean;
  hasMultiple: boolean;
  onChanged: () => void;
  ownerKind: string;
  onDeleteItem: () => void;
}) {
  // The engine is an optional dependency. Everything else on this tab — the version list, renaming, the
  // default-version pick — is database-side and works without it, so only the convert control keys off this.
  const { data: transcode } = useQuery({
    queryKey: ["transcode-availability"],
    queryFn: () => mediaServer.transcodeAvailability(),
    staleTime: 5 * 60 * 1000,
    // The endpoint is admin-only, and only an admin sees the controls it gates — asking as a plain viewer
    // would just be a 401 on every version row.
    enabled: canManage,
  });
  const disc = isBluraySource(source);
  const requiresConversion = disc && (!diagnostic?.reason || diagnostic.reason === "requires_conversion");
  const [blurayOpen, setBlurayOpen] = useState(false);
  const canConvert = !disc && canManage && (transcode?.available ?? false);
  const [tracksOpen, setTracksOpen] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [editOpen, setEditOpen] = useState(false);
  // The picture — the film, not a cover a muxer wrote as a video track. Its badges carry the Dolby Vision
  // profile when it is recorded, and a dual-layer profile 7 gets the one note a viewer with Apple hardware needs.
  const picture = pictureStream(source.streams);
  const rangeBadges = dynamicRangeBadges(picture?.hdrFormat, picture?.dolbyVision);
  const rangeNote = dolbyVisionNote(picture?.dolbyVision);

  // The default only matters when a title has several versions; clients play MediaSources[0].
  const showDefault = hasMultiple && !disc;

  // File meta: container · duration · overall bitrate. Size has its own comparison column. The container often omits an overall
  // bitrate (typical for MKV), so fall back to the average derived from size ÷ duration — display only.
  const bitrateKbps =
    source.bitrate != null && source.bitrate > 0
      ? Math.round(source.bitrate / 1000)
      : source.durationTicks > 0
        ? Math.round((source.sizeBytes * 8) / (source.durationTicks / 1e7) / 1000)
        : null;
  const metaParts = [
    source.container,
    disc ? null : formatRuntime(source.durationTicks),
    bitrateKbps ? `${bitrateKbps.toLocaleString()} kbps` : null,
  ].filter(Boolean);
  const groups = groupStreams(source.streams);
  const embeddedCount = groups.reduce((count, group) => count + group.streams.length, 0);

  const setDefault = useMutation({
    mutationFn: (next: boolean) => mediaServer.setDefaultSource(itemId, next ? source.id : null),
    onSuccess: () => {
      onChanged();
      toast.success(isDefault ? "Default version cleared" : "Set as default version");
    },
    onError: (error) => toast.error("Couldn’t update default version", { description: errorMessage(error) }),
  });

  return (
    <TableBody id={`source-${source.id}`} className="scroll-mt-6 border-b last:border-0">
      <TableRow className="[&>td]:whitespace-normal">
        <TableCell>
          <div className="flex items-center gap-2">
            {embeddedCount > 0 ? (
              <Button
                variant="ghost"
                size="icon-sm"
                aria-label={`${tracksOpen ? "Hide" : "Show"} tracks in ${source.fileName}`}
                aria-expanded={tracksOpen}
                onClick={() => setTracksOpen(open => !open)}
              >
                <ChevronDown className={cn("transition-transform", !tracksOpen && "-rotate-90")} />
              </Button>
            ) : <span className="w-7 shrink-0" />}
            {disc ? <Disc3 className="text-warning-foreground size-4 shrink-0" aria-label="Blu-ray source" /> : <Package className="text-muted-foreground size-4 shrink-0" aria-hidden />}
            <div className="min-w-0">
              <p className="font-medium break-all">{source.fileName}</p>
              <p className="text-muted-foreground mt-0.5">{[...metaParts, embeddedCount ? `${embeddedCount} ${embeddedCount === 1 ? "track" : "tracks"}` : null].filter(Boolean).join(" · ")}</p>
            </div>
          </div>
        </TableCell>
        <TableCell className="text-muted-foreground">{disc ? "Blu-ray" : "Container"}</TableCell>
        <TableCell>
          {picture ? <TrackText stream={picture} /> : <span className="text-muted-foreground">{disc ? "Blu-ray disc" : "Not probed"}</span>}
          <div className="mt-0.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-muted-foreground">
            {groups.filter(group => group.type !== "Video").map(group => {
              const Icon = STREAM_ICONS[group.type] ?? FileQuestion;
              const label = group.type === "Subtitle" && group.streams.length === 1 ? "subtitle" : group.label.toLowerCase();
              return <span key={group.type} className="inline-flex items-center gap-1"><Icon className="size-3 shrink-0" aria-hidden />{group.streams.length} {label}</span>;
            })}
            {rangeBadges.map(badge => <Badge key={badge} variant="secondary">{badge}</Badge>)}
          </div>
          {rangeNote && !diagnostic?.dolbyVisionFallback ? <span className="text-warning-foreground">{rangeNote}</span> : null}
        </TableCell>
        <TableCell className="tabular-nums">{formatBytes(source.sizeBytes)}</TableCell>
        <TableCell>
          <div className="flex flex-col gap-1" aria-label="Source diagnostics">
            <div className="flex flex-wrap gap-1">
              {requiresConversion
                ? <Badge variant="warning"><AlertTriangle aria-hidden />Requires conversion</Badge>
                : <ManagementAvailability value={diagnostic?.availability ?? "unknown"} />}
              {highlightMatches && diagnostic?.matchesFilter && <Badge variant="info">Matches filters</Badge>}
            </div>
            {!requiresConversion && diagnostic?.reason && <span className="text-muted-foreground">{diagnosticReason(diagnostic.reason)}</span>}
            {diagnostic?.dolbyVisionFallback && <span className="text-warning-foreground flex items-start gap-1"><AlertTriangle className="mt-0.5 size-3 shrink-0" aria-hidden />Dolby Vision profile 7 · HDR10 fallback on Apple/tvOS</span>}
            {diagnostic?.dolbyVisionUnknown && <span className="text-warning-foreground flex items-start gap-1"><AlertTriangle className="mt-0.5 size-3 shrink-0" aria-hidden />Dolby Vision profile unknown</span>}
            {diagnostic?.reason === "storage_unavailable" && <Link className="underline" href="/settings?tab=catalogs">Catalog settings</Link>}
            {!disc && <IndexingIndicator id={source.id} snapshot={source.indexing} />}
          </div>
        </TableCell>
        <TableCell>
          {canManage && (
            <div className="flex flex-wrap items-center gap-0.5">
              {showDefault && (
                <Button
                  variant="ghost"
                  size="icon-sm"
                  aria-label={isDefault ? "Clear default version" : "Set as default version"}
                  disabled={setDefault.isPending}
                  onClick={() => setDefault.mutate(!isDefault)}
                >
                  <Star className={cn(isDefault && "fill-current text-warning-foreground")} />
                </Button>
              )}
              <Button variant="ghost" size="icon-sm" aria-label="Rename version" onClick={() => setEditOpen(true)}>
                <Pencil />
              </Button>
              {disc && <Tooltip>
                <TooltipTrigger render={<span className="inline-flex" tabIndex={!transcode?.blurayImport ? 0 : undefined} />}>
                  <Button variant="ghost" size="icon-sm" aria-label="Create MKV" disabled={!transcode?.blurayImport} onClick={() => setBlurayOpen(true)}>
                    <PackagePlus aria-hidden />
                  </Button>
                </TooltipTrigger>
                <TooltipContent>{transcode?.blurayImport ? "Create MKV" : "Connect a Transcode Engine with Blu-ray import support to create MKV."}</TooltipContent>
              </Tooltip>}
              <Button
                variant="ghost"
                size="icon-sm"
                aria-label={hasMultiple ? "Delete this version" : `Delete ${ownerKind === "Episode" ? "episode" : "movie"}`}
                className="text-destructive hover:text-destructive hover:bg-destructive/10"
                onClick={() => hasMultiple ? setDeleteOpen(true) : onDeleteItem()}
              >
                <Trash2 />
              </Button>
            </div>
          )}
        </TableCell>
      </TableRow>
      {tracksOpen && groups.flatMap(group => group.streams.map(stream => (
        <TrackRow key={stream.id} stream={stream} isDefault={stream.index === group.defaultIndex}
          selection={selectionControl(stream)}
          actions={canManage && <Button variant="ghost" size="icon-sm" aria-label={`Extract ${trackLabel(stream)}`}
            disabled={!canConvert || extracting || !!extractionReason(stream)} title={extractionReason(stream) ?? (!canConvert ? "Transcode Engine is unavailable." : "Extract track to a file")}
            onClick={() => extractTrack(stream)}><FileOutput /></Button>} />
      )))}
      <SidecarRows
        sidecars={source.streams.filter(stream => stream.isExternal)}
        onChanged={onChanged}
        canManage={canManage}
        selectionControl={selectionControl}
      />
      {canManage && <EditVersionDialog source={source} open={editOpen} onOpenChange={setEditOpen} onChanged={onChanged} />}
      {disc && canManage && blurayOpen && <BlurayDialog source={source} runtimeTicks={runtimeTicks} onClose={() => setBlurayOpen(false)} />}
      {canManage && <DeleteVersionDialog source={source} open={deleteOpen} onOpenChange={setDeleteOpen} onChanged={onChanged} onDeleteItem={onDeleteItem} />}
    </TableBody>
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

// Separate audio/subtitle files stay visible when the container is collapsed. Their selection and delete
// actions remain file-level operations; embedded tracks only change through the conversion/extraction dialogs.
function SidecarRows({
  sidecars,
  onChanged,
  canManage,
  selectionControl,
}: {
  sidecars: MediaStream[];
  onChanged: () => void;
  canManage: boolean;
  selectionControl: (stream: MediaStream) => ReactNode;
}) {

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

  return (
    <>
      {groupByType(sidecars).flatMap(group => group.streams.map(stream => (
        <TrackRow
          key={stream.id}
          stream={stream}
          selection={selectionControl(stream)}
          actions={canManage && (
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
        />
      )))}
    </>
  );
}

/**
 * The transcode jobs of these items — a movie's own, or every episode's of a series — polled while any is
 * active. When the last one finishes, `onSettled` runs so the freshly produced version appears where the
 * versions are listed. Completed and cancelled jobs stay in Activity; failures remain visible here.
 * Admin-only, like the endpoint it reads.
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
  const visible = mine.filter(job => isTranscodeActive(job) || job.state === "Failed");

  const previousActive = useRef(activeCount);
  useEffect(() => {
    if (previousActive.current > 0 && activeCount === 0) {
      onSettled();
    }
    previousActive.current = activeCount;
  }, [activeCount, onSettled]);

  if (visible.length === 0) {
    return null;
  }

  // A label over a stack of conversion cards, mirroring the Activity page's groups — each job already draws
  // its own bordered card, so this block adds no box of its own.
  return (
    <section className="flex flex-col gap-2">
      <p className="text-muted-foreground text-xs font-medium">Conversions</p>
      <div className="flex flex-col gap-3">
        {visible.map((job) => (
          <TranscodeJobRow key={job.id} job={job} showManagementLink={false} />
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
  onDeleteItem,
}: {
  source: LibraryMediaSource;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onChanged: () => void;
  onDeleteItem: () => void;
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
    onError: (error) => {
      if (error instanceof ApiError && error.status === 409 && error.code === "last_media_source") {
        onChanged();
        onOpenChange(false);
        onDeleteItem();
        return;
      }
      toast.error("Couldn’t remove version", { description: errorMessage(error) });
    },
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
