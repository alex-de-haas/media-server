"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useId, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, BellRing, CalendarPlus, Check, Clapperboard, Clock, ExternalLink, FolderInput, Heart, Image as ImageIcon, MoreVertical, RefreshCw, Star, Trash2, User, Wand2 } from "lucide-react";
import { toast } from "@/lib/toast";
import {
  mediaServer,
  type CastMember,
  type ChildDeleteResult,
  type Episode,
  type LibraryDetail,
  type LibraryMoveJob,
  type Network,
  type SeasonSummary,
  type Studio,
  type UserItemData,
} from "@/lib/media-server";
import { MediaSources } from "@/components/media-sources";
import { personHref } from "@/components/poster-card";
import { PosterPickerDialog } from "@/components/poster-picker-dialog";
import { RemapDialog } from "@/components/remap-dialog";
import { StarRating } from "@/components/star-rating";
import { useTrackedTitle } from "@/components/track-title-control";
import { MoveToCatalogDialog } from "@/components/move-to-catalog-dialog";
import { ApiError } from "@/lib/api";
import { MovieWatchHistory } from "@/components/movie-watch-history";
import { RelatedMovies } from "@/components/related-movies";
import { ReminderDialog } from "@/components/reminder-dialog";
import { WatchTimeDialog } from "@/components/watch-time-dialog";
import { QUERIES_AFFECTED_BY_HISTORY_CHANGE } from "@/lib/watch-history-calendar";
import { episodeLabel, episodeMediaLine, formatAirDate, formatEta, formatRuntime, formatSpeed } from "@/lib/format";
import { errorMessage, formatCount, openExternal } from "@/lib/ui";
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
import { Carousel, CarouselContent, CarouselItem, CarouselNext, CarouselPrevious } from "@/components/ui/carousel";
import { Checkbox } from "@/components/ui/checkbox";
import { DropdownMenu, DropdownMenuContent, DropdownMenuGroup, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Progress } from "@/components/ui/progress";
import { WatchControls } from "@/components/watch-controls";
import { Accordion, AccordionContent, AccordionItem, AccordionTrigger } from "@/components/ui/accordion";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { useSession } from "@/components/app-shell";
import { managementHref, managementApi } from "@/lib/management";
import { AvailabilityNotice, useLibraryAvailability } from "@/components/library-availability";

/** Personal browsing detail with compact media summaries and links to administrator management. */
export function MediaDetail({ id, backHref, backLabel }: { id: string; backHref: string; backLabel: string }) {
  const router = useRouter();
  const [deleteReason, setDeleteReason] = useState<"item" | "lastVersion" | null>(null);
  const detail = useQuery({
    queryKey: ["library-detail", id], queryFn: () => mediaServer.getLibraryDetail(id),
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 2,
  });
  useEffect(() => {
    if (detail.data?.removedAt && detail.error instanceof ApiError && detail.error.status === 404) {
      router.replace(backHref);
      toast.success("This movie is no longer in your removed list");
    }
  }, [detail.data?.removedAt, detail.error, router, backHref]);

  if (detail.isPending) {
    return <MediaDetailSkeleton />;
  }

  if (detail.isError || !detail.data) {
    return (
      <div className="flex flex-col gap-3">
        <BackLink href={backHref} label={backLabel} />
        <p className="text-muted-foreground text-sm">This item could not be found.</p>
      </div>
    );
  }

  const item = detail.data;
  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-center justify-between gap-2">
        <BackLink href={backHref} label={backLabel} />
        <ItemActions item={item} backHref={backHref} deleteReason={deleteReason} onDeleteRequest={setDeleteReason} />
      </div>
      <Hero item={item} />
      {!item.removedAt && <TitleAvailability id={item.id} />}
      {item.kind === "Movie" && <MovieWatchHistory key={item.id} id={item.id} title={item.title} removed={!!item.removedAt} />}
      <CastCarousel key={`cast-${item.id}`} cast={item.cast} />
      {!item.removedAt && <DetailMedia item={item} />}
      {item.kind === "Movie" && <RelatedMovies id={item.id} backHref={backHref} />}
      <section aria-label="Tags" className="flex min-w-0 flex-col gap-3">
        <h2 className="text-lg font-semibold tracking-tight">Tags</h2>
        <KeywordTags keywords={item.keywords} />
      </section>
    </div>
  );
}

function DetailMedia({ item }: { item: LibraryDetail }) {
  const mediaLabel = item.kind === "Series" ? "Episodes" : "Media";

  return (
    <section aria-label={mediaLabel} className="flex min-w-0 flex-col gap-3">
      <h2 className="text-lg font-semibold tracking-tight">{mediaLabel}</h2>

      {item.kind === "Series" ? (
        <SeriesEpisodes seriesId={item.id} seasons={item.seasons} />
      ) : (
        <p className="text-muted-foreground text-sm">{item.mediaSources.length} {item.mediaSources.length === 1 ? "version" : "versions"} · {Array.from(new Set(item.mediaSources.flatMap(source => source.streams.filter(stream => stream.type === "Video" && !stream.isExternal).map(stream => stream.displayTitle || stream.codec).filter(Boolean)))).join(" · ") || "Media information pending"}</p>
      )}
    </section>
  );
}

function TitleAvailability({ id }: { id: string }) {
  const availability = useLibraryAvailability([id]);
  return <AvailabilityNotice value={availability.get(id)} />;
}

/** Shared source editor, mounted only by the guarded management surface. */
export function ManagedItem({ id, backHref, query = "", seriesId, sourceId, showActions = true }: {
  id: string; backHref: string; query?: string; seriesId?: string; sourceId?: string; showActions?: boolean;
}) {
  const detail = useQuery({ queryKey: ["library-detail", id], queryFn: () => mediaServer.getLibraryDetail(id), retry: false });
  const diagnostics = useQuery({ queryKey: ["management", "sources", id, query], queryFn: () => managementApi.sources(id, query) });
  const moving = useActiveMove(seriesId ?? id) !== undefined;
  const focusedSource = useRef<string | null>(null);
  useEffect(() => {
    if (!sourceId || !detail.data || focusedSource.current === sourceId) return;
    const element = document.getElementById(`source-${sourceId}`);
    if (element) { element.scrollIntoView({ block: "nearest" }); focusedSource.current = sourceId; }
  }, [sourceId, detail.data]);
  const [deleteReason, setDeleteReason] = useState<"item" | "lastVersion" | null>(null);
  if (detail.isPending) return <p role="status">Loading versions…</p>;
  if (detail.isError) return <div className="flex items-center gap-3"><p>This item is unavailable or has been merged.</p><Button variant="outline" onClick={() => detail.refetch()}>Retry</Button><Link href={backHref}>Back to list</Link></div>;
  const item = detail.data;
  return <div className="flex min-w-0 flex-col gap-2">
    <div className={showActions ? "flex items-center justify-between gap-2" : "contents"}>{showActions && <span className="font-medium">{item.title}</span>}<ItemActions management showTrigger={showActions} moveOwnerId={seriesId} item={item} backHref={backHref} deleteReason={deleteReason} onDeleteRequest={setDeleteReason} /></div>
    {item.removedAt ? <p className="text-muted-foreground text-sm">Removed from the library. Retained personal history remains available in the library.</p> : <>
      <MoveProgress itemId={seriesId ?? id} />
      {diagnostics.isError && <div role="alert">Diagnostics could not be loaded. <Button variant="link" onClick={() => diagnostics.refetch()}>Retry</Button></div>}
      <MediaSources owner={{ id, kind: item.kind, title: item.title, runtimeTicks: item.runtimeTicks }} sources={item.mediaSources} defaultSourceId={item.defaultSourceId}
        location={<ContentLocation catalogRoot={item.catalogRoot} path={item.contentPath} />}
        moving={moving} diagnostics={diagnostics.data} highlightMatches={hasDiagnosticFilter(query)} onDeleteItem={() => setDeleteReason("lastVersion")} />
    </>}
  </div>;
}

function hasDiagnosticFilter(query: string) {
  const params = new URLSearchParams(query);
  return ["sourceKind", "resolution", "codec", "hdr", "dvProfile", "state"].some(key => params.has(key)) || !["all", "active", null].includes(params.get("view"));
}

export function ManagementTitleActions({ id, backHref, seriesId, libraryHref }: { id: string; backHref: string; seriesId?: string; libraryHref?: string }) {
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState<"item" | "lastVersion" | null>(null);
  const detail = useQuery({ queryKey: ["library-detail", id], queryFn: () => mediaServer.getLibraryDetail(id), enabled: open });
  return open && detail.data ? <ItemActions management moveOwnerId={seriesId} libraryHref={libraryHref} defaultOpen item={detail.data} backHref={backHref} deleteReason={reason} onDeleteRequest={setReason} />
    : <Button variant="ghost" size="icon-sm" aria-label={detail.isError ? "Retry actions" : "More actions"} aria-haspopup="menu" aria-busy={detail.isFetching} title={detail.isError ? "Retry actions" : "More actions"} disabled={detail.isFetching} onClick={() => { setOpen(true); if (detail.isError) void detail.refetch(); }}><MoreVertical aria-hidden /></Button>;
}

// The in-flight cross-catalog move for this item, if any. Shares the ["library-move-jobs"] cache with the
// Activity view: seeded from the admin-only active list, then kept live by RealtimeBridge over SSE.
function useActiveMove(itemId: string): LibraryMoveJob | undefined {
  const { role } = useSession();
  const moves = useQuery({
    queryKey: ["library-move-jobs"],
    queryFn: mediaServer.listActiveMoves,
    enabled: role === "admin",
  });
  return (moves.data ?? []).find((move) => move.itemId === itemId);
}

// A move to another catalog in flight for this item — the Media-section counterpart of the Conversions block,
// with a live per-byte progress bar pushed over SSE. While it runs, mutations of the item and its sources
// are disabled here and rejected by the API (the move is relocating these very files).
function MoveProgress({ itemId }: { itemId: string }) {
  const move = useActiveMove(itemId);
  if (!move) {
    return null;
  }

  return (
    <div className="flex flex-col gap-2 rounded-md border border-dashed p-3">
      <p className="text-muted-foreground text-xs font-medium">
        Moving to {move.targetCatalogName ?? "another catalog"}…
      </p>
      {move.queued ? (
        // Waiting behind the move that's copying now — no bar/stats yet, just say it's queued.
        <span className="text-muted-foreground flex items-center gap-1.5 text-xs">
          <Clock className="size-3.5 shrink-0" aria-hidden />
          Queued
        </span>
      ) : (
        <>
          <div className="flex items-center gap-2">
            <Progress value={move.progress} className="h-1.5" />
            <span className="text-muted-foreground shrink-0 font-mono text-xs tabular-nums">{move.progress}%</span>
          </div>
          {(move.bytesPerSecond != null || move.etaSeconds != null) && (
            <div className="text-muted-foreground flex flex-wrap gap-x-3 font-mono text-xs tabular-nums">
              {/* Each shown only when present — the final 100% tick reports a rate but no ETA. */}
              {move.bytesPerSecond != null && <span>{formatSpeed(move.bytesPerSecond)}</span>}
              {move.etaSeconds != null && <span>ETA {formatEta(move.etaSeconds)}</span>}
            </div>
          )}
        </>
      )}
    </div>
  );
}

// The full folder path in Management, combining the catalog root and the title's relative folder.
function ContentLocation({
  catalogRoot,
  path,
}: {
  catalogRoot: string;
  path: string | null;
}) {
  const fullPath = catalogRoot && path
    ? `${catalogRoot.replace(/\/+$/, "")}/${path.replace(/^\/+/, "")}`
    : catalogRoot || path || "—";
  return (
    <dl className="text-muted-foreground flex min-w-0 items-center gap-1.5 text-xs">
      <dt className="shrink-0">Path</dt>
      <dd className="text-foreground truncate font-mono" title={fullPath}>{fullPath}</dd>
    </dl>
  );
}

function MediaDetailSkeleton() {
  return (
    <div className="flex flex-col gap-6">
      <Skeleton className="h-5 w-28" />
      <div className="flex gap-4 sm:gap-6">
        <Skeleton className="aspect-[2/3] w-28 shrink-0 self-start rounded-md sm:w-40" />
        <div className="flex flex-1 flex-col gap-3 pt-2">
          <Skeleton className="h-9 w-2/3" />
          <Skeleton className="h-4 w-40" />
          <div className="mt-2 flex gap-2">
            <Skeleton className="h-9 w-32" />
            <Skeleton className="h-9 w-28" />
          </div>
        </div>
      </div>
      <Skeleton className="h-14 w-full max-w-2xl" />
    </div>
  );
}

/** Personal actions, provider links, and role-gated library administration. */
export function ItemActions({ item, backHref, deleteReason, onDeleteRequest, management = false, moveOwnerId, libraryHref, defaultOpen = false, showTrigger = true }: {
  showTrigger?: boolean;
  moveOwnerId?: string;
  libraryHref?: string;
  defaultOpen?: boolean;
  management?: boolean;
  item: LibraryDetail;
  backHref: string;
  deleteReason: "item" | "lastVersion" | null;
  onDeleteRequest: (reason: "item" | "lastVersion" | null) => void;
}) {
  const { id, title, kind, catalogId } = item;
  const removed = !!item.removedAt;
  const [trackingOpen, setTrackingOpen] = useState(false);
  const tracked = useTrackedTitle(item.tmdbId, kind);
  const canTrack = !management && !!item.tmdbId && (kind === "Movie" || kind === "Series");
  const hasReminder = tracked?.reminders.some((reminder) => reminder.active) ?? false;
  const tmdbUrl = canTrack ? `https://www.themoviedb.org/${kind === "Movie" ? "movie" : "tv"}/${item.tmdbId}` : null;
  const { role } = useSession();
  const router = useRouter();
  const queryClient = useQueryClient();
  const confirmOpen = deleteReason !== null;
  const setConfirmOpen = (open: boolean) => onDeleteRequest(open ? "item" : null);
  const [remapOpen, setRemapOpen] = useState(false);
  const [moveOpen, setMoveOpen] = useState(false);
  const [logWatchOpen, setLogWatchOpen] = useState(false);
  const [posterOpen, setPosterOpen] = useState(false);
  // While a move is relocating this item's files, everything that mutates the item or reads its files is
  // locked (the API rejects it with a 409 anyway) — only the provider-side metadata refresh stays usable.
  const moving = useActiveMove(moveOwnerId ?? id) !== undefined;

  const remove = useMutation({
    mutationFn: async (options: DeleteItemOptions): Promise<ChildDeleteResult | void> => kind === "Episode" ? mediaServer.deleteEpisode(id, options.deleteFiles, options.deleteUserData) : mediaServer.deleteLibraryItem(id, options.deleteFiles, options.deleteUserData),
    onSuccess: (result) => {
      for (const key of [["management"], ["library-availability"], ["library-detail"], ["episodes"], ["library"], ["recent"], ["resume"], ["nextup"]]) {
        queryClient.invalidateQueries({ queryKey: key });
      }
      router.push(result?.seriesRemoved ? "/manage/series" : backHref);
      toast.success("Item deleted");
    },
    onError: (error) => toast.error("Couldn’t delete item", { description: errorMessage(error) }),
  });

  const purge = useMutation({
    mutationFn: () => mediaServer.purgeRemovedTitle(id),
    onSuccess: () => {
      for (const queryKey of QUERIES_AFFECTED_BY_HISTORY_CHANGE) void queryClient.invalidateQueries({ queryKey });
      router.replace(backHref);
      toast.success("Removed title deleted permanently");
    },
    onError: (error) => toast.error(errorMessage(error)),
  });

  const refresh = useMutation({
    mutationFn: () => mediaServer.refreshMetadata(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["library-detail", id] });
      queryClient.invalidateQueries({ queryKey: ["management"] });
      toast.success("Metadata refreshed");
    },
    onError: (error) => toast.error("Couldn’t refresh metadata", { description: errorMessage(error) }),
  });

  // Re-probes the file(s) with ffprobe and rewrites the stored streams — picks up media data (codecs,
  // languages, track titles) that wasn't captured at import time, without a full library rescan. Asked of
  // a series, the server fans out over its episodes, so their summary lines are refreshed too.
  const refreshMedia = useMutation({
    mutationFn: () => mediaServer.refreshMedia(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["library-detail"] });
      queryClient.invalidateQueries({ queryKey: ["episodes", id] });
      queryClient.invalidateQueries({ queryKey: ["management"] });
      queryClient.invalidateQueries({ queryKey: ["library-availability"] });
      toast.success("Media data refreshed");
    },
    onError: (error) => toast.error("Couldn’t refresh media data", { description: errorMessage(error) }),
  });

  // A viewing the server never observed — the watched toggle claims no time, so only this puts a play
  // on a day of the calendar. Offered on movies: a season-wide fan-out at one instant is a different
  // gesture, and an episode has its own row in the list rather than this menu.
  const logWatch = useMutation({
    mutationFn: (watchedAt: string) => mediaServer.logWatch(id, watchedAt),
    onSuccess: () => {
      setLogWatchOpen(false);
      for (const key of QUERIES_AFFECTED_BY_HISTORY_CHANGE) {
        queryClient.invalidateQueries({ queryKey: key });
      }
      toast.success("Watch logged");
    },
    // The dialog stays open on failure, so the time the user picked is still there to retry with.
    onError: (error) => toast.error("Couldn’t log this watch", { description: errorMessage(error) }),
  });

  const canLogWatch = !management && kind === "Movie";
  if (role !== "admin" && !canLogWatch && !canTrack && !item.imdbId && !item.trailerUrl) {
    return null;
  }

  return (
    <>
      {showTrigger && <DropdownMenu defaultOpen={defaultOpen}>
        <DropdownMenuTrigger
          render={
            <Button variant="ghost" size="icon-sm" aria-label="More actions">
              <MoreVertical />
            </Button>
          }
        />
        <DropdownMenuContent>
          {libraryHref && <>
            <DropdownMenuGroup>
              <DropdownMenuItem render={<Link href={libraryHref} />}>
                <ExternalLink aria-hidden /> Open in library
              </DropdownMenuItem>
            </DropdownMenuGroup>
            <DropdownMenuSeparator />
          </>}
          {role === "admin" && !management && <DropdownMenuItem render={<Link href={managementHref(kind, id, undefined, item.removedAt ? { removed: "1" } : undefined)} />}>Manage</DropdownMenuItem>}
          {canLogWatch && (
            <DropdownMenuItem onClick={() => setLogWatchOpen(true)}>
              <CalendarPlus />
              Log watch…
            </DropdownMenuItem>
          )}
          {canTrack && (
            <DropdownMenuItem onClick={() => setTrackingOpen(true)} className={cn(tracked && "text-brand")}>
              {hasReminder ? <BellRing /> : <CalendarPlus />}
              {tracked ? "Tracked" : "Track / remind me"}
            </DropdownMenuItem>
          )}
          {!management && item.trailerUrl && (
            <DropdownMenuItem onClick={() => openExternal(item.trailerUrl!)}>
              <Clapperboard /> Trailer
            </DropdownMenuItem>
          )}
          {!management && item.imdbId && (
            <DropdownMenuItem onClick={() => openExternal(`https://www.imdb.com/title/${item.imdbId}/`)}>
              <ExternalLink /> View on IMDb
            </DropdownMenuItem>
          )}
          {tmdbUrl && (
            <DropdownMenuItem onClick={() => openExternal(tmdbUrl)}>
              <ExternalLink /> View on TMDb
            </DropdownMenuItem>
          )}
          {(canLogWatch || canTrack || item.imdbId || item.trailerUrl) && role === "admin" && management && <DropdownMenuSeparator />}
          {role === "admin" && management && removed && (
            <DropdownMenuItem variant="destructive" onClick={() => setConfirmOpen(true)}>
              <Trash2 /> Delete permanently…
            </DropdownMenuItem>
          )}
          {role === "admin" && management && !removed && (
            <>
              {kind !== "Episode" && <>
              <DropdownMenuItem disabled={refresh.isPending} onClick={() => refresh.mutate()}>
                <RefreshCw className={cn(refresh.isPending && "animate-spin")} aria-hidden />
                Refresh metadata
              </DropdownMenuItem>
              <DropdownMenuItem disabled={refreshMedia.isPending || moving} onClick={() => refreshMedia.mutate()}>
                <Clapperboard className={cn(refreshMedia.isPending && "animate-pulse")} aria-hidden />
                Refresh media data
              </DropdownMenuItem>
              {/* The artwork ranking prefers a poster that carries a title, but TMDb does not always have
                  one — this is where the operator overrides it for a title that came out ambiguous. */}
              <DropdownMenuItem onClick={() => setPosterOpen(true)}>
                <ImageIcon />
                Choose poster…
              </DropdownMenuItem>
              </>}
              {/* Series are corrected per episode (in the episode list), not at the series level. */}
              {kind !== "Series" && (
                <DropdownMenuItem disabled={moving} onClick={() => setRemapOpen(true)}>
                  <Wand2 />
                  Fix match…
                </DropdownMenuItem>
              )}
              {kind !== "Episode" && <DropdownMenuItem disabled={moving} onClick={() => setMoveOpen(true)}>
                <FolderInput />
                {moving ? "Moving to catalog…" : "Move to catalog…"}
              </DropdownMenuItem>}
              <DropdownMenuItem variant="destructive" disabled={moving} onClick={() => setConfirmOpen(true)}>
                <Trash2 />
                Delete…
              </DropdownMenuItem>
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>}

      {item.tmdbId && (kind === "Movie" || kind === "Series") && (
        <ReminderDialog
          target={{ trackedTitleId: tracked?.trackedTitleId, providerRef: { provider: "tmdb", id: item.tmdbId }, kind, title, year: item.year, posterUrl: item.posterUrl }}
          showTrackOnly={!tracked}
          open={trackingOpen}
          onOpenChange={setTrackingOpen}
        />
      )}
      {canLogWatch && (
        <WatchTimeDialog
          open={logWatchOpen}
          onOpenChange={setLogWatchOpen}
          heading="Log a watch"
          description={
            <>
              Record a viewing of <span className="text-foreground font-medium">{title}</span> the server
              never saw — it appears in your Watched calendar on that day.
            </>
          }
          confirmLabel="Log watch"
          pending={logWatch.isPending}
          onSubmit={(watchedAt) => logWatch.mutate(watchedAt)}
        />
      )}

      {/* Gated with the menu items that open them: a viewer who cannot reach the action has no use for
          its dialog in the tree. */}
      {role === "admin" && management && removed && (
        <AlertDialog open={confirmOpen} onOpenChange={setConfirmOpen}>
          <AlertDialogContent>
            <AlertDialogHeader>
              <AlertDialogTitle>Delete {title} permanently?</AlertDialogTitle>
              <AlertDialogDescription>Deletes the retained movie and every user’s history, ratings and favorites. This cannot be undone.</AlertDialogDescription>
            </AlertDialogHeader>
            <AlertDialogFooter>
              <AlertDialogCancel disabled={purge.isPending}>Cancel</AlertDialogCancel>
              <AlertDialogAction variant="destructive" disabled={purge.isPending} onClick={() => purge.mutate()}>Delete permanently</AlertDialogAction>
            </AlertDialogFooter>
          </AlertDialogContent>
        </AlertDialog>
      )}
      {role === "admin" && management && !removed && catalogId && (
        <>
          <DeleteItemDialog
            open={confirmOpen}
            onOpenChange={setConfirmOpen}
            heading={kind === "Episode" ? "Delete episode?" : deleteReason === "lastVersion" ? "Delete movie?" : undefined}
            title={title}
            detail={deleteReason === "lastVersion" ? `This is the last version. It cannot be removed separately; delete the ${kind === "Episode" ? "episode" : "movie"} instead.` : undefined}
            onConfirm={(options) => {
              remove.mutate(options);
              setConfirmOpen(false);
            }}
          />

          <PosterPickerDialog itemId={id} title={title} open={posterOpen} onOpenChange={setPosterOpen} />

          <RemapDialog
            itemId={id}
            mode={kind === "Episode" ? "episode" : "movie"}
            defaultSeason={item.parentIndexNumber ?? 1}
            defaultEpisode={item.indexNumber ?? 1}
            currentTitle={title}
            open={remapOpen}
            onOpenChange={setRemapOpen}
            onRemapped={async (targetId) => {
              setRemapOpen(false);
              for (const key of [["management"], ["library-availability"], ["library-detail"], ["episodes"], ["library"], ["recent"], ["resume"], ["nextup"]]) {
                queryClient.invalidateQueries({ queryKey: key });
              }
              // An episode can keep its ID while changing series; resolve its current owner after remapping.
              if (kind === "Episode") {
                try {
                  const target = await managementApi.location(targetId);
                  router.replace(managementHref(target.kind, target.id, target.seriesId ?? undefined));
                } catch (error) {
                  toast.error("Couldn’t open the corrected episode", { description: errorMessage(error) });
                  router.replace("/manage/series");
                }
              } else if (targetId !== id) {
                router.replace(managementHref(kind, targetId, undefined, { return: backHref }));
              } else {
                queryClient.invalidateQueries({ queryKey: ["library-detail", id] });
              }
            }}
          />

          <MoveToCatalogDialog
            itemId={id}
            itemKind={kind}
            itemTitle={title}
            currentCatalogId={catalogId}
            open={moveOpen}
            onOpenChange={setMoveOpen}
            onMoveStarted={() => {
              // The move runs in the background; progress stays in the expanded management detail.
              // The library views refresh now and again from the job's completion event; if a merge removes
              // this item, its detail refetch after completion surfaces "not found" with the back link.
              for (const key of [["management"], ["library-availability"], ["library-detail"], ["episodes"], ["library"], ["recent"], ["resume"], ["nextup"]]) {
                queryClient.invalidateQueries({ queryKey: key });
              }
            }}
          />
        </>
      )}
    </>
  );
}

export type DeleteItemOptions = { deleteFiles: boolean; deleteUserData: boolean };

function DeleteItemDialog({
  open,
  onOpenChange,
  heading = "Delete item?",
  title,
  detail,
  onConfirm,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  heading?: string;
  title: string;
  // Extra line under the description — what else goes with this delete (a season's episode count).
  detail?: string;
  onConfirm: (options: DeleteItemOptions) => void;
}) {
  // Default to keeping files: deleting a published item shouldn't silently remove the media on disk.
  // History is likewise kept by default — watched state and favorites survive as a hidden tombstone
  // and come back if the title is ever re-added.
  const [deleteFiles, setDeleteFiles] = useState(false);
  const [deleteUserData, setDeleteUserData] = useState(false);
  const deleteFilesId = useId();
  const deleteUserDataId = useId();

  // Re-apply the defaults every time the dialog (re)opens so a prior toggle (then cancel) doesn't carry over.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setDeleteFiles(false);
      setDeleteUserData(false);
    }
  }

  return (
    <AlertDialog open={open} onOpenChange={onOpenChange}>
      <AlertDialogContent className="sm:max-w-md">
        <AlertDialogHeader>
          <AlertDialogTitle>{heading}</AlertDialogTitle>
          <AlertDialogDescription>
            Remove <span className="text-foreground font-medium">{title}</span> from the library.
            {detail && <span className="mt-1 block">{detail}</span>}
          </AlertDialogDescription>
        </AlertDialogHeader>

        <div className="flex items-start gap-2 rounded-md border p-3 text-sm">
          <Checkbox
            id={deleteFilesId}
            className="mt-0.5"
            checked={deleteFiles}
            onCheckedChange={(checked) => setDeleteFiles(checked === true)}
          />
          <label htmlFor={deleteFilesId} className="cursor-pointer">
            Delete files from disk
            <span className="text-muted-foreground block text-xs">
              Also removes the media files from disk. Otherwise only the library entry is removed.
            </span>
          </label>
        </div>

        <div className="flex items-start gap-2 rounded-md border p-3 text-sm">
          <Checkbox
            id={deleteUserDataId}
            className="mt-0.5"
            checked={deleteUserData}
            onCheckedChange={(checked) => setDeleteUserData(checked === true)}
          />
          <label htmlFor={deleteUserDataId} className="cursor-pointer">
            Also delete watch history and favorites
            <span className="text-muted-foreground block text-xs">
              Otherwise they are kept and restored if the title is ever added back.
            </span>
          </label>
        </div>

        <AlertDialogFooter>
          <AlertDialogCancel size="sm">Cancel</AlertDialogCancel>
          <AlertDialogAction
            variant="destructive"
            size="sm"
            onClick={() => onConfirm({ deleteFiles, deleteUserData })}
          >
            {deleteFiles ? "Delete + remove files" : "Remove from library"}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

function BackLink({ href, label }: { href: string; label: string }) {
  return (
    <Link href={href} className="text-muted-foreground hover:text-foreground inline-flex w-fit items-center gap-1.5 text-sm">
      <ArrowLeft className="size-4" aria-hidden /> {label}
    </Link>
  );
}

function Hero({ item }: { item: LibraryDetail }) {
  const queryClient = useQueryClient();
  const [clearMark, setClearMark] = useState<"rating" | "favorite" | null>(null);
  const invalidate = (data: UserItemData) => {
    setClearMark(null);
    queryClient.setQueryData<LibraryDetail>(["library-detail", item.id], (previous) =>
      previous ? { ...previous, userData: data } : previous);
    for (const key of QUERIES_AFFECTED_BY_HISTORY_CHANGE) {
      void queryClient.invalidateQueries({ queryKey: key });
    }
  };
  const favorite = useMutation({ mutationFn: (value: boolean) => mediaServer.setFavorite(item.id, value), onSuccess: invalidate, onError: (error) => toast.error(errorMessage(error)) });
  const rating = useMutation({
    mutationFn: (value: number | null) => mediaServer.setRating(item.id, value),
    onSuccess: invalidate,
    onError: (error) => toast.error(errorMessage(error)),
  });

  const isPlayed = item.userData?.played ?? false;
  const isFavorite = item.userData?.isFavorite ?? false;
  // Only works are ratable: the engine collapses an episode into its series, so "more like episode 4"
  // is not a question anything downstream can ask.
  const isRatable = item.kind === "Movie" || item.kind === "Series";
  const resume = !item.removedAt && !isPlayed && item.userData?.playedPercentage ? Math.min(item.userData.playedPercentage, 100) : null;
  const runtime = formatRuntime(item.runtimeTicks);
  const seriesCounts =
    item.kind === "Series" && item.seasonCount ? `${item.seasonCount} season${item.seasonCount === 1 ? "" : "s"}` : null;
  const meta = [item.year?.toString(), runtime, seriesCounts, item.genres.slice(0, 3).join(", ") || null]
    .filter(Boolean)
    .join(" · ");

  return (
    // Full-bleed cinematic banner: breaks out of the centered content column to span the viewport width
    // (see the overflow-x-clip note in AppShell). The backdrop fills the band; scrims keep text legible.
    <div className="relative left-1/2 right-1/2 -mr-[50vw] -ml-[50vw] w-screen overflow-hidden border-y bg-secondary">
      {item.backdropUrl && (
        // eslint-disable-next-line @next/next/no-img-element
        <img src={item.backdropUrl} alt="" className="absolute inset-0 h-full w-full object-cover opacity-70" />
      )}
      {/* The backdrop runs the full height of the banner — poster, title and description — fading downward
          so the artwork stays bright up top and is only lightly darkened behind the description. */}
      <div className="from-background/85 via-background/40 absolute inset-0 bg-linear-to-t to-transparent" />
      <div className="from-background/65 absolute inset-0 bg-linear-to-r to-transparent" />
      <div className="relative mx-auto flex w-full max-w-5xl flex-col gap-6 px-6 pt-6 pb-10 sm:pt-8">
        <div className="flex flex-col gap-4 sm:flex-row sm:gap-6">
          <div className="bg-background/40 aspect-[2/3] w-28 shrink-0 self-start overflow-hidden rounded-md shadow-lg ring-1 ring-black/10 sm:w-40">
            {item.posterUrl && (
              // eslint-disable-next-line @next/next/no-img-element
              <img src={item.posterUrl} alt={item.title} className="h-full w-full object-cover" />
            )}
          </div>
          <div className="flex flex-col gap-3">
            <div className="flex flex-col gap-2">
              {item.logoUrl ? (
                <>
                  {/* eslint-disable-next-line @next/next/no-img-element */}
                  <img
                    src={item.logoUrl}
                    alt={item.title}
                    className="max-h-20 w-auto max-w-[16rem] object-contain object-left drop-shadow-[0_1px_6px_rgb(0_0_0/0.55)] sm:max-h-28 sm:max-w-sm"
                  />
                  {/* The logo is the visible title; keep a real heading for screen readers and the page outline. */}
                  <h1 className="sr-only">{item.title}</h1>
                </>
              ) : (
                <h1 className="text-3xl leading-tight font-semibold tracking-tight sm:text-4xl">{item.title}</h1>
              )}
              {item.removedAt && <Badge variant="secondary">Removed from library</Badge>}
              {meta && <p className="text-muted-foreground text-sm">{meta}</p>}
              <div className="text-muted-foreground flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
                {item.officialRating && (
                  <Badge variant="outline" className="font-normal">
                    {item.officialRating}
                  </Badge>
                )}
                {item.communityRating != null && (
                  <span className="flex items-center gap-1">
                    <Star className="text-brand size-4" aria-hidden /> {item.communityRating.toFixed(1)}
                    {item.voteCount != null ? <span className="text-xs">({formatCount(item.voteCount)})</span> : null}
                  </span>
                )}
                {item.kind === "Series" && item.status && <span className="text-xs">{item.status}</span>}
              </div>
              <CreditLine item={item} />
              {item.collectionName && <p className="text-muted-foreground text-xs">Part of {item.collectionName}</p>}
              {item.kind === "Series" ? (
                <BrandSummary label="Networks" brands={item.networks ?? []} />
              ) : (
                <BrandSummary label="Studios" brands={item.studios} />
              )}
            </div>

            {resume != null && (
              <div className="max-w-xs">
                <div className="bg-secondary h-1 overflow-hidden rounded-full">
                  <div className="bg-brand h-full" style={{ width: `${resume}%` }} />
                </div>
                <p className="text-muted-foreground mt-1 text-xs">{Math.round(resume)}% watched</p>
              </div>
            )}

            <div className="flex flex-wrap gap-2">
              <WatchControls id={item.id} title={item.title} userData={item.userData} removed={!!item.removedAt} statusOnly={item.kind === "Series"} showLogWatch={false} />
              <Button
                variant="outline"
                onClick={() => item.removedAt && isFavorite ? setClearMark("favorite") : favorite.mutate(!isFavorite)}
                disabled={favorite.isPending}
                aria-label={isFavorite ? "Remove favorite" : "Add favorite"}
                className={cn(isFavorite && "border-brand text-brand")}
              >
                {/* A heart, not a star: the star row beside it is the rating, and two controls sharing
                    one mark reads as two versions of the same gesture. */}
                <Heart className={cn("size-4", isFavorite && "fill-brand")} aria-hidden /> Favorite
              </Button>
              {isRatable && (
                <StarRating
                  value={item.userData?.userRating ?? null}
                  pending={rating.isPending}
                  onChange={(next) => item.removedAt && next === null ? setClearMark("rating") : rating.mutate(next)}
                />
              )}
            </div>
          </div>
        </div>
        {/* Overview lives inside the banner so the backdrop runs underneath it before fading out. */}
        {item.overview && <p className="max-w-2xl text-sm leading-relaxed">{item.overview}</p>}
        <AlertDialog open={clearMark !== null} onOpenChange={(open) => !open && setClearMark(null)}>
          <AlertDialogContent>
            <AlertDialogHeader>
              <AlertDialogTitle>Clear your {clearMark}?</AlertDialogTitle>
              <AlertDialogDescription>
                If this is your last watch, rating or favorite, {item.title} disappears from your removed list.
                Its record is deleted when nobody has any marks left.
              </AlertDialogDescription>
            </AlertDialogHeader>
            <AlertDialogFooter>
              <AlertDialogCancel disabled={rating.isPending || favorite.isPending}>Cancel</AlertDialogCancel>
              <AlertDialogAction disabled={rating.isPending || favorite.isPending}
                onClick={() => clearMark === "rating" ? rating.mutate(null) : favorite.mutate(false)}>Clear {clearMark}</AlertDialogAction>
            </AlertDialogFooter>
          </AlertDialogContent>
        </AlertDialog>
      </div>
    </div>
  );
}

// Keep studio/network metadata available without letting mixed external logos dominate the hero.
function BrandSummary({ label, brands }: { label: string; brands: (Network | Studio)[] }) {
  if (brands.length === 0) {
    return null;
  }

  const [primary, ...rest] = brands;
  const names = brands.map((brand) => brand.name).join(", ");
  const value = rest.length > 0 ? `${primary.name} +${rest.length}` : primary.name;

  return (
    <p className="text-muted-foreground max-w-xl truncate text-xs" title={`${label}: ${names}`}>
      <span className="text-foreground/70">{label}: </span>
      {value}
    </p>
  );
}

// "Directed by …" for movies, "Created by …" for series. Renders nothing when the credit is unknown.
function CreditLine({ item }: { item: LibraryDetail }) {
  const names = item.kind === "Series" ? item.creators : item.directors;
  if (names.length === 0) {
    return null;
  }

  return (
    <p className="text-muted-foreground text-sm">
      {item.kind === "Series" ? "Created by " : "Directed by "}
      <span className="text-foreground">{names.join(", ")}</span>
    </p>
  );
}

// Top-billed cast with headshots; a person without a photo falls back to a placeholder icon. Each member
// links to their person page (the cast DTO always carries a stable person identity).
function CastCarousel({ cast }: { cast: CastMember[] }) {
  if (!cast.length) {
    return (
      <section aria-label="Cast" className="flex flex-col gap-3">
        <h2 className="text-lg font-semibold tracking-tight">Cast</h2>
        <EmptyDetailPanel>No cast information available.</EmptyDetailPanel>
      </section>
    );
  }

  return (
    <Carousel aria-label="Cast" opts={{ align: "start", slidesToScroll: "auto", breakpoints: { "(prefers-reduced-motion: reduce)": { duration: 0 } } }} className="flex min-w-0 flex-col gap-3">
      <div className="flex items-center justify-between gap-3">
        <h2 className="text-lg font-semibold tracking-tight">Cast</h2>
        {cast.length > 1 && (
          <div className="flex gap-2">
            <CarouselPrevious aria-label="Previous cast members" className="static translate-none" />
            <CarouselNext aria-label="Next cast members" className="static translate-none" />
          </div>
        )}
      </div>
      <CarouselContent className="-ml-3 touch-pan-y py-1">
        {cast.map((member, index) => (
          <CarouselItem key={`${member.provider}-${member.providerId}:${member.character ?? ""}`} aria-label={`${index + 1} of ${cast.length}`} className="basis-[44%] pl-3 sm:basis-1/4 lg:basis-1/6">
            <CastCard member={member} />
          </CarouselItem>
        ))}
      </CarouselContent>
    </Carousel>
  );
}

function CastCard({ member }: { member: CastMember }) {
  return (
    <Link href={personHref(member.provider, member.providerId)} className="group flex flex-col gap-2 rounded-md outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2">
      <div className="bg-secondary aspect-[2/3] w-full overflow-hidden rounded-md ring-1 ring-black/5 transition group-hover:opacity-90">
        {member.profileUrl ? (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={member.profileUrl} alt={member.name} loading="lazy" draggable={false} className="h-full w-full object-cover" />
        ) : (
          <div className="text-muted-foreground flex h-full w-full items-center justify-center">
            <User className="size-8" aria-hidden />
          </div>
        )}
      </div>
      <div className="min-w-0">
        <p className="truncate text-sm font-medium transition-colors group-hover:text-brand">{member.name}</p>
        {member.character && <p className="text-muted-foreground truncate text-xs">{member.character}</p>}
      </div>
    </Link>
  );
}

// TMDb keyword tags — a lightweight "themes" cloud below the cast.
function KeywordTags({ keywords }: { keywords: string[] }) {
  if (!keywords.length) {
    return <EmptyDetailPanel>No tags available.</EmptyDetailPanel>;
  }

  return (
    <section className="flex flex-col gap-3">
      <div className="flex flex-wrap gap-2">
        {keywords.map((keyword) => (
          <Badge key={keyword} variant="secondary" className="font-normal capitalize">
            {keyword}
          </Badge>
        ))}
      </div>
    </section>
  );
}

interface SeasonGroup {
  key: string;
  seasonId: string | null;
  seasonNumber: number;
  episodes: Episode[];
}

// Seasons come from the detail's rollup first, so a season whose episodes are all gone — one holding only
// extras, which the backend deliberately keeps — still gets a heading and stays deletable. Episodes with no
// matching season row (or when the rollup is empty) fall back to grouping by season number, so the listing
// never depends on the rollup being there.
function groupIntoSeasons(seasons: SeasonSummary[], episodes: Episode[]): SeasonGroup[] {
  const groups = new Map<string, SeasonGroup>();
  for (const season of seasons) {
    groups.set(season.id, {
      key: season.id,
      seasonId: season.id,
      seasonNumber: season.seasonNumber ?? 0,
      episodes: [],
    });
  }

  for (const episode of episodes) {
    const known = episode.seasonId ? groups.get(episode.seasonId) : undefined;
    if (known) {
      known.episodes.push(episode);
      continue;
    }

    const seasonNumber = episode.seasonNumber ?? 0;
    const key = `number:${seasonNumber}`;
    const group = groups.get(key) ?? { key, seasonId: episode.seasonId ?? null, seasonNumber, episodes: [] };
    group.episodes.push(episode);
    groups.set(key, group);
  }

  return [...groups.values()].sort((a, b) => a.seasonNumber - b.seasonNumber);
}

function SeriesEpisodes({
  seriesId,
  seasons,
}: {
  seriesId: string;
  seasons: SeasonSummary[] | null;
}) {
  const episodes = useQuery({ queryKey: ["episodes", seriesId], queryFn: () => mediaServer.listEpisodes(seriesId) });
  // A move relocates the whole show, so every episode's controls lock together.
  const availability = useLibraryAvailability((episodes.data ?? []).map(episode => episode.id));

  if (episodes.isPending) {
    return <p className="text-muted-foreground text-sm">Loading episodes…</p>;
  }

  const groups = groupIntoSeasons(seasons ?? [], episodes.data ?? []);
  if (!groups.length) {
    return <EmptyDetailPanel>No episodes available.</EmptyDetailPanel>;
  }



  return (
    <section className="flex flex-col gap-4">
      <Accordion key={seriesId} multiple defaultValue={[]} className="rounded-lg border">
        {groups.map((group) => (
          <AccordionItem key={group.key} value={group.key}>
            <div className="relative">
              <AccordionTrigger className="px-4 py-3">
                <span className="pr-10">
                  Season {group.seasonNumber}
                  <span className="text-muted-foreground font-normal">
                    {" · "}{formatCount(group.episodes.length)} {group.episodes.length === 1 ? "episode" : "episodes"}
                  </span>
                </span>
              </AccordionTrigger>
            </div>
            <AccordionContent className="px-3 pb-3">
              {group.episodes.length ? (
                <ul className="flex flex-col divide-y">
                  {group.episodes.map((episode) => (
                    <EpisodeRow key={episode.id} episode={episode} seriesId={seriesId} availability={availability.get(episode.id)} />
                  ))}
                </ul>
              ) : (
                <p className="text-muted-foreground text-sm">No episodes in this season.</p>
              )}
            </AccordionContent>
          </AccordionItem>
        ))}
      </Accordion>
    </section>
  );
}

// Invalidates every view a change under a series can affect — a delete, a remap, a version produced or
// removed. Shared so the row, the season heading and the media surface cannot drift apart. Every detail
// query goes, not only the series': an expanded episode holds its own, and it is the one that changed.
function useEpisodeInvalidation(seriesId: string) {
  const queryClient = useQueryClient();
  return () => {
    for (const key of [["management"], ["library-availability"], ["episodes", seriesId], ["library-detail"], ["library"], ["nextup"], ["resume"], ["recent"]]) {
      queryClient.invalidateQueries({ queryKey: key });
    }
  };
}

// Deleting the last episode prunes its season, and a series left with nothing under it goes too — in which
// case this page no longer exists and we head back to the library.
function useAfterChildDelete(seriesId: string, backHref: string) {
  const invalidate = useEpisodeInvalidation(seriesId);
  const router = useRouter();
  return (result: ChildDeleteResult, removed: string) => {
    invalidate();
    if (result.seriesRemoved) {
      toast.success(`${removed} deleted — the series had nothing left and was removed too`);
      router.push(backHref);
      return;
    }
    toast.success(`${removed} deleted`);
  };
}

export function SeasonDeleteControl({
  season,
  seasonId,
  episodeCount,
  seriesId,
  backHref,
}: {
  season: number;
  // The season row the delete targets — null for episodes published without one, which offer no season action.
  seasonId: string | null;
  episodeCount: number;
  seriesId: string;
  backHref: string;
}) {
  const { role } = useSession();
  const moving = useActiveMove(seriesId) !== undefined;
  const [open, setOpen] = useState(false);
  const afterDelete = useAfterChildDelete(seriesId, backHref);

  const remove = useMutation({
    mutationFn: (options: DeleteItemOptions) => mediaServer.deleteSeason(seasonId!, options.deleteFiles, options.deleteUserData),
    onSuccess: (result) => {
      setOpen(false);
      afterDelete(result, `Season ${season}`);
    },
    onError: (error) => toast.error(errorMessage(error)),
  });

  if (role !== "admin" || !seasonId) {
    return null;
  }

  return (
    <>
      <Button
        variant="ghost"
        size="icon-sm"
        aria-label={`Delete season ${season}`}
        disabled={remove.isPending || moving}
        onClick={() => setOpen(true)}
      >
        <Trash2 />
      </Button>
      <DeleteItemDialog
        open={open}
        onOpenChange={setOpen}
        heading="Delete season?"
        title={`Season ${season}`}
        detail={
          episodeCount
            ? `${formatCount(episodeCount)} ${episodeCount === 1 ? "episode" : "episodes"} will be removed from the library.`
            : "This season holds no episodes — only its extras, if any, will be removed."
        }
        onConfirm={(options) => remove.mutate(options)}
      />
    </>
  );
}

function EmptyDetailPanel({ children }: { children: string }) {
  return <p className="text-muted-foreground py-6 text-sm">{children}</p>;
}

function EpisodeRow({
  episode,
  seriesId,
  availability,
}: {
  episode: Episode;
  seriesId: string;
  availability?: import("@/lib/management").LibraryAvailability;
}) {
  const { role } = useSession();
  const isPlayed = episode.userData?.played ?? false;
  const resume = !isPlayed && episode.userData?.playedPercentage ? Math.min(episode.userData.playedPercentage, 100) : null;
  const runtime = formatRuntime(episode.runtimeTicks);
  // When it aired and how long it runs, on one line under the title.
  const facts = [formatAirDate(episode.airDate), runtime].filter(Boolean).join(" · ");
  // A double-episode file reads "S01E01-E02", so the season no longer looks like it skipped an episode.
  // The title stays the first episode's — TMDb has no combined title for the range and one is not invented.
  const label = episodeLabel(episode.seasonNumber, episode.episodeNumber, episode.episodeNumberEnd);

  return (
    <li className="flex flex-col">
      {/* Two groups that may wrap: the still with its facts, and the actions. On a phone an admin's full
          set of controls beside a 96px still would leave the title no width at all, so the actions drop
          under the facts, right-aligned, once the row cannot hold both side by side. */}
      <div className="flex flex-wrap items-start gap-3 p-3 text-sm">
        <div className="flex min-w-0 grow basis-64 gap-3">
          {/* The episode's still, at the 16:9 the provider cut it at. A row without one shows a placeholder
              rather than borrowing the show's backdrop, which would make every row look the same. Lazy, since
              a long-running show lists hundreds of rows at once. The watched mark sits in its corner, where a
              poster card carries it; the control that flips it is among the row's actions on the right. */}
          <div className="bg-secondary relative aspect-video w-24 shrink-0 overflow-hidden rounded-md sm:w-40">
            {episode.stillUrl ? (
              // Decorative: the row's title names the episode.
              // eslint-disable-next-line @next/next/no-img-element
              <img src={episode.stillUrl} alt="" loading="lazy" className="h-full w-full object-cover" />
            ) : (
              <div aria-hidden className="text-muted-foreground flex h-full items-center justify-center text-xs">
                No preview
              </div>
            )}
            {isPlayed && (
              <span
                className="bg-brand text-brand-foreground absolute top-1.5 right-1.5 flex size-5 items-center justify-center rounded-full"
                aria-label="Watched"
              >
                <Check className="size-3.5" aria-hidden />
              </span>
            )}
          </div>
          <div className="min-w-0 flex-1">
            <p className="truncate">
              <span className="text-muted-foreground font-mono text-xs">{label}</span> {episode.title}
            </p>
            {facts && <p className="text-muted-foreground mt-0.5 text-xs">{facts}</p>}
            {/* The synopsis is clamped so a season scans as a list; opening the row — which is where the
                rest of the episode lives — lets it run to its end. */}
            {episode.overview && (
              <p className={cn("text-muted-foreground mt-1 text-xs", "line-clamp-3")}>{episode.overview}</p>
            )}
            {/* What is on disk, at a glance: the default version's picture and size, and how many versions
                there are. The full surface — every version, track and control — is one click down. */}
            <p className="text-muted-foreground mt-1 truncate font-mono text-xs">{episodeMediaLine(episode.media)}</p>
            <AvailabilityNotice value={availability} />
            {resume != null && (
              <span className="bg-secondary mt-1 block h-1 max-w-32 overflow-hidden rounded-full">
                <span className="bg-brand block h-full" style={{ width: `${resume}%` }} />
              </span>
            )}
          </div>
        </div>
        <div className="ml-auto flex max-w-full flex-wrap items-center gap-3">
          <WatchControls id={episode.id} title={`${label} ${episode.title}`} userData={episode.userData} />
          {role === "admin" && <Link className="text-primary text-xs underline" href={managementHref("Episode", episode.id, seriesId)}>Manage</Link>}
        </div>
      </div>
    </li>
  );
}
