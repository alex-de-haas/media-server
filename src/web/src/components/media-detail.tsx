"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useId, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, CalendarPlus, Check, ChevronDown, Clapperboard, Clock, ExternalLink, FolderInput, Heart, Image as ImageIcon, Link2, MoreVertical, Play, RefreshCw, Star, Trash2, User, Wand2 } from "lucide-react";
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
} from "@/lib/media-server";
import { Conversions, MediaSources } from "@/components/media-sources";
import { infuseDeepLink, openInfuse } from "@/lib/infuse";
import { personHref } from "@/components/poster-card";
import { PosterPickerDialog } from "@/components/poster-picker-dialog";
import { RemapDialog } from "@/components/remap-dialog";
import { StarRating } from "@/components/star-rating";
import { TrackTitleControl } from "@/components/track-title-control";
import { MoveToCatalogDialog } from "@/components/move-to-catalog-dialog";
import { WatchTimeDialog } from "@/components/watch-time-dialog";
import { QUERIES_AFFECTED_BY_HISTORY_CHANGE } from "@/lib/watch-history-calendar";
import { episodeLabel, episodeMediaLine, formatEta, formatRuntime, formatSpeed } from "@/lib/format";
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
import { Checkbox } from "@/components/ui/checkbox";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Progress } from "@/components/ui/progress";
import { Separator } from "@/components/ui/separator";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { cn } from "@/lib/utils";
import { useSession } from "@/components/app-shell";

/** Movie or series detail page. Branches on `kind`: a movie's Media tab lists its versions; a series lists its
 * episodes, each of which opens onto the same media surface. */
export function MediaDetail({ id, backHref, backLabel }: { id: string; backHref: string; backLabel: string }) {
  const detail = useQuery({ queryKey: ["library-detail", id], queryFn: () => mediaServer.getLibraryDetail(id) });

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
        <ItemActions id={item.id} title={item.title} kind={item.kind} catalogId={item.catalogId} backHref={backHref} />
      </div>
      <Hero item={item} />
      <DetailTabs item={item} backHref={backHref} />
    </div>
  );
}

function DetailTabs({ item, backHref }: { item: LibraryDetail; backHref: string }) {
  const mediaLabel = item.kind === "Series" ? "Episodes" : "Media";

  return (
    <Tabs defaultValue="cast" className="gap-4">
      <div className="min-w-0 border-b">
        <TabsList variant="line" aria-label="Media detail sections">
          <TabsTrigger value="cast">Cast</TabsTrigger>
          <TabsTrigger value="media">{mediaLabel}</TabsTrigger>
          <TabsTrigger value="tags">Tags</TabsTrigger>
        </TabsList>
      </div>

      <TabsContent value="cast">
        <CastList cast={item.cast} />
      </TabsContent>
      <TabsContent value="media">
        <div className="flex flex-col gap-3">
          <ContentLocation catalogName={item.catalogName} catalogRoot={item.catalogRoot} path={item.contentPath} />
          <MoveProgress itemId={item.id} />
          {item.kind === "Series" ? (
            <SeriesEpisodes seriesId={item.id} seasons={item.seasons} backHref={backHref} />
          ) : (
            <MovieMedia item={item} />
          )}
        </div>
      </TabsContent>
      <TabsContent value="tags">
        <KeywordTags keywords={item.keywords} />
      </TabsContent>
    </Tabs>
  );
}

// A movie's Media tab: the shared media surface, owned by the movie and locked while it moves.
function MovieMedia({ item }: { item: LibraryDetail }) {
  const moving = useActiveMove(item.id) !== undefined;
  return (
    <MediaSources
      owner={{ id: item.id, kind: item.kind, title: item.title }}
      sources={item.mediaSources}
      defaultSourceId={item.defaultSourceId}
      moving={moving}
    />
  );
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

// A move to another catalog in flight for this item — the Media-tab counterpart of the Conversions block,
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

// Where the title lives on disk, shown atop the media/episodes tab: the catalog it belongs to, that
// catalog's root host path, and the catalog-root-relative folder holding its files (when on disk).
function ContentLocation({
  catalogName,
  catalogRoot,
  path,
}: {
  catalogName: string;
  catalogRoot: string;
  path: string | null;
}) {
  return (
    <dl className="text-muted-foreground bg-secondary/40 grid grid-cols-[auto_1fr] items-baseline gap-x-3 gap-y-1 rounded-md border px-3 py-2 text-xs">
      <dt>Catalog</dt>
      <dd className="text-foreground break-words">{catalogName || "—"}</dd>
      <dt>Path</dt>
      <dd className="text-foreground font-mono break-all">{catalogRoot || "—"}</dd>
      {path && (
        <>
          <dt>Folder</dt>
          <dd className="text-foreground font-mono break-all">{path}</dd>
        </>
      )}
    </dl>
  );
}

function MediaDetailSkeleton() {
  return (
    <div className="flex flex-col gap-6">
      <Skeleton className="h-5 w-28" />
      <div className="flex gap-4 sm:gap-6">
        <Skeleton className="aspect-[2/3] w-28 shrink-0 rounded-md sm:w-40" />
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

/**
 * The page's overflow menu. It carries two kinds of action: logging a watch, which any signed-in user
 * may do to their own history, and the admin block that used to be all of it. Both live behind one `⋮`
 * because the button row belongs to what a viewer does on most visits — a fourth button there would
 * compete with `Mark watched` for the same glance, and logging a past viewing is the rare gesture.
 */
function ItemActions({ id, title, kind, catalogId, backHref }: { id: string; title: string; kind: string; catalogId: string; backHref: string }) {
  const { role } = useSession();
  const router = useRouter();
  const queryClient = useQueryClient();
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [remapOpen, setRemapOpen] = useState(false);
  const [moveOpen, setMoveOpen] = useState(false);
  const [logWatchOpen, setLogWatchOpen] = useState(false);
  const [posterOpen, setPosterOpen] = useState(false);
  // While a move is relocating this item's files, everything that mutates the item or reads its files is
  // locked (the API rejects it with a 409 anyway) — only the provider-side metadata refresh stays usable.
  const moving = useActiveMove(id) !== undefined;

  const remove = useMutation({
    mutationFn: (options: DeleteItemOptions) => mediaServer.deleteLibraryItem(id, options.deleteFiles, options.deleteUserData),
    onSuccess: () => {
      for (const key of [["library"], ["recent"], ["resume"], ["nextup"]]) {
        queryClient.invalidateQueries({ queryKey: key });
      }
      router.push(backHref);
      toast.success("Item deleted");
    },
    onError: (error) => toast.error("Couldn’t delete item", { description: errorMessage(error) }),
  });

  const refresh = useMutation({
    mutationFn: () => mediaServer.refreshMetadata(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["library-detail", id] });
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

  const canLogWatch = kind === "Movie";
  if (role !== "admin" && !canLogWatch) {
    return null;
  }

  return (
    <>
      <DropdownMenu>
        <DropdownMenuTrigger
          render={
            <Button variant="ghost" size="icon-sm" aria-label="More actions">
              <MoreVertical />
            </Button>
          }
        />
        <DropdownMenuContent>
          {canLogWatch && (
            <DropdownMenuItem onClick={() => setLogWatchOpen(true)}>
              <CalendarPlus />
              Log watch…
            </DropdownMenuItem>
          )}
          {canLogWatch && role === "admin" && <DropdownMenuSeparator />}
          {role === "admin" && (
            <>
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
              {/* Series are corrected per episode (in the episode list), not at the series level. */}
              {kind !== "Series" && (
                <DropdownMenuItem disabled={moving} onClick={() => setRemapOpen(true)}>
                  <Wand2 />
                  Fix match…
                </DropdownMenuItem>
              )}
              <DropdownMenuItem disabled={moving} onClick={() => setMoveOpen(true)}>
                <FolderInput />
                {moving ? "Moving to catalog…" : "Move to catalog…"}
              </DropdownMenuItem>
              <DropdownMenuItem variant="destructive" disabled={moving} onClick={() => setConfirmOpen(true)}>
                <Trash2 />
                Delete…
              </DropdownMenuItem>
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>

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
      {role === "admin" && (
        <>
          <DeleteItemDialog
            open={confirmOpen}
            onOpenChange={setConfirmOpen}
            title={title}
            onConfirm={(options) => {
              remove.mutate(options);
              setConfirmOpen(false);
            }}
          />

          <PosterPickerDialog itemId={id} title={title} open={posterOpen} onOpenChange={setPosterOpen} />

          <RemapDialog
            itemId={id}
            mode="movie"
            currentTitle={title}
            open={remapOpen}
            onOpenChange={setRemapOpen}
            onRemapped={(targetId) => {
              setRemapOpen(false);
              for (const key of [["library"], ["recent"], ["resume"], ["nextup"]]) {
                queryClient.invalidateQueries({ queryKey: key });
              }
              // The corrected movie is a different item — navigate to its detail page.
              if (targetId !== id) {
                router.replace(`${backHref}/${targetId}`);
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
              // The move runs in the background — stay here and watch it on the Media tab (like a conversion).
              // The library views refresh now and again from the job's completion event; if a merge removes
              // this item, its detail refetch after completion surfaces "not found" with the back link.
              for (const key of [["library"], ["recent"], ["resume"], ["nextup"]]) {
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
  const invalidate = () => {
    for (const key of [["library-detail", item.id], ["library"], ["resume"], ["nextup"], ["recent"]]) {
      queryClient.invalidateQueries({ queryKey: key });
    }
  };
  const played = useMutation({ mutationFn: (value: boolean) => mediaServer.setPlayed(item.id, value), onSuccess: invalidate });
  const favorite = useMutation({ mutationFn: (value: boolean) => mediaServer.setFavorite(item.id, value), onSuccess: invalidate });
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
  const resume = !isPlayed && item.userData?.playedPercentage ? Math.min(item.userData.playedPercentage, 100) : null;
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
          <div className="bg-background/40 aspect-[2/3] w-28 shrink-0 overflow-hidden rounded-md shadow-lg ring-1 ring-black/10 sm:w-40">
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
              <InfuseLaunch item={item} />
              <Button
                variant="outline"
                onClick={() => played.mutate(!isPlayed)}
                disabled={played.isPending}
                className={cn(isPlayed && "border-brand text-brand")}
              >
                <Check className="size-4" aria-hidden /> {isPlayed ? "Watched" : "Mark watched"}
              </Button>
              <Button
                variant="outline"
                onClick={() => favorite.mutate(!isFavorite)}
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
                  onChange={(next) => rating.mutate(next)}
                />
              )}
              {item.tmdbId && (item.kind === "Movie" || item.kind === "Series") && (
                <TrackTitleControl
                  tmdbId={item.tmdbId}
                  kind={item.kind}
                  title={item.title}
                  year={item.year}
                  posterUrl={item.posterUrl}
                />
              )}
              {item.trailerUrl && (
                <Button variant="outline" onClick={() => openExternal(item.trailerUrl!)}>
                  <Clapperboard className="size-4" aria-hidden /> Trailer
                </Button>
              )}
              {item.imdbId && (
                <Button
                  variant="secondary"
                  aria-label="View on IMDb"
                  className="border-transparent bg-[#f5c518] text-black hover:bg-[#e4b915] hover:text-black"
                  onClick={() => openExternal(`https://www.imdb.com/title/${item.imdbId}/`)}
                >
                  <span className="font-semibold tracking-normal">IMDb</span>
                  <ExternalLink className="size-4" aria-hidden />
                </Button>
              )}
            </div>
          </div>
        </div>
        {/* Overview lives inside the banner so the backdrop runs underneath it before fading out. */}
        {item.overview && <p className="max-w-2xl text-sm leading-relaxed">{item.overview}</p>}
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
function CastList({ cast }: { cast: CastMember[] }) {
  if (!cast.length) {
    return <EmptyDetailPanel>No cast information available.</EmptyDetailPanel>;
  }

  return (
    <section className="flex flex-col gap-3">
      <ul className="grid grid-cols-2 gap-4 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6">
        {cast.map((member) => (
          <li key={`${member.provider}-${member.providerId}:${member.character ?? ""}`}>
            <CastCard member={member} />
          </li>
        ))}
      </ul>
    </section>
  );
}

function CastCard({ member }: { member: CastMember }) {
  return (
    <Link href={personHref(member.provider, member.providerId)} className="group flex flex-col gap-2">
      <div className="bg-secondary aspect-[2/3] w-full overflow-hidden rounded-md ring-1 ring-black/5 transition group-hover:opacity-90">
        {member.profileUrl ? (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={member.profileUrl} alt={member.name} className="h-full w-full object-cover" />
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

// Opens a trailer / IMDb page in a new tab, severing the opener for safety. The `noopener` window
// feature already nulls `opener`, but not every browser honours it, so clear it explicitly too.
/**
 * Launches Infuse for the item via a TMDb library deep link (movies auto-play; series open to the show),
 * with a copy-link fallback for when the popup is blocked or Infuse isn't installed. Renders nothing when
 * the item has no TMDb id to deep-link to.
 */
function InfuseLaunch({ item }: { item: LibraryDetail }) {
  const isSeries = item.kind === "Series";
  const deepLink = isSeries
    ? infuseDeepLink({ kind: "series", tmdbId: item.tmdbId })
    : infuseDeepLink({ kind: "movie", tmdbId: item.tmdbId }, { play: true });

  if (!deepLink) {
    return null;
  }

  return (
    <>
      <Button onClick={() => openInfuse(deepLink)}>
        <Play className="size-4" aria-hidden /> {isSeries ? "Open in Infuse" : "Play in Infuse"}
      </Button>
      <Button variant="outline" size="icon" aria-label="Copy Infuse link" onClick={() => copyInfuseLink(deepLink)}>
        <Link2 className="size-4" aria-hidden />
      </Button>
    </>
  );
}

async function copyInfuseLink(deepLink: string) {
  try {
    await navigator.clipboard.writeText(deepLink);
    toast.success("Infuse link copied");
  } catch {
    // Clipboard can be denied; show the link so the operator can copy it by hand.
    toast.error("Couldn’t copy the link", { description: deepLink });
  }
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
  backHref,
}: {
  seriesId: string;
  seasons: SeasonSummary[] | null;
  backHref: string;
}) {
  const { role } = useSession();
  const episodes = useQuery({ queryKey: ["episodes", seriesId], queryFn: () => mediaServer.listEpisodes(seriesId) });
  // A move relocates the whole show, so every episode's controls lock together.
  const moving = useActiveMove(seriesId) !== undefined;
  const invalidate = useEpisodeInvalidation(seriesId);

  if (episodes.isPending) {
    return <p className="text-muted-foreground text-sm">Loading episodes…</p>;
  }

  const groups = groupIntoSeasons(seasons ?? [], episodes.data ?? []);
  if (!groups.length) {
    return <EmptyDetailPanel>No episodes available.</EmptyDetailPanel>;
  }

  const episodeIds = (episodes.data ?? []).map((episode) => episode.id);

  return (
    <section className="flex flex-col gap-4">
      {/* Every episode's jobs in one place, where a movie's Media tab keeps its own: a job card names its
          output file, which carries the episode code, so nothing is lost by not listing them per row. */}
      {role === "admin" && episodeIds.length > 0 && <Conversions itemIds={episodeIds} onSettled={invalidate} />}
      {groups.map((group) => (
        <div key={group.key} className="flex flex-col gap-2">
          <div className="flex items-center gap-2">
            <h2 className="flex-1 text-lg font-semibold tracking-tight">Season {group.seasonNumber}</h2>
            <SeasonDeleteControl
              season={group.seasonNumber}
              seasonId={group.seasonId}
              episodeCount={group.episodes.length}
              seriesId={seriesId}
              backHref={backHref}
            />
          </div>
          <Separator />
          {group.episodes.length ? (
            <ul className="flex flex-col divide-y rounded-md border">
              {group.episodes.map((episode) => (
                <EpisodeRow key={episode.id} episode={episode} seriesId={seriesId} moving={moving} backHref={backHref} />
              ))}
            </ul>
          ) : (
            <p className="text-muted-foreground text-sm">No episodes in this season.</p>
          )}
        </div>
      ))}
    </section>
  );
}

// Invalidates every view a change under a series can affect — a delete, a remap, a version produced or
// removed. Shared so the row, the season heading and the media surface cannot drift apart. Every detail
// query goes, not only the series': an expanded episode holds its own, and it is the one that changed.
function useEpisodeInvalidation(seriesId: string) {
  const queryClient = useQueryClient();
  return () => {
    for (const key of [["episodes", seriesId], ["library-detail"], ["library"], ["nextup"], ["resume"], ["recent"]]) {
      queryClient.invalidateQueries({ queryKey: key });
    }
  };
}

// Deleting the last episode prunes its season, and a series left with nothing under it goes too — in which
// case this page no longer exists and we head back to the library.
function useAfterChildDelete(seriesId: string, backHref: string) {
  const router = useRouter();
  const invalidate = useEpisodeInvalidation(seriesId);
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

function SeasonDeleteControl({
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
        disabled={remove.isPending}
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
  moving,
  backHref,
}: {
  episode: Episode;
  seriesId: string;
  moving: boolean;
  backHref: string;
}) {
  const { role } = useSession();
  const [remapOpen, setRemapOpen] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [expanded, setExpanded] = useState(false);
  const invalidate = useEpisodeInvalidation(seriesId);
  const afterDelete = useAfterChildDelete(seriesId, backHref);

  const played = useMutation({
    mutationFn: (value: boolean) => mediaServer.setPlayed(episode.id, value),
    onSuccess: invalidate,
  });

  const isPlayed = episode.userData?.played ?? false;
  const resume = !isPlayed && episode.userData?.playedPercentage ? Math.min(episode.userData.playedPercentage, 100) : null;
  const runtime = formatRuntime(episode.runtimeTicks);
  // A double-episode file reads "S01E01-E02", so the season no longer looks like it skipped an episode.
  // The title stays the first episode's — TMDb has no combined title for the range and one is not invented.
  const label = episodeLabel(episode.seasonNumber, episode.episodeNumber, episode.episodeNumberEnd);

  const remove = useMutation({
    mutationFn: (options: DeleteItemOptions) => mediaServer.deleteEpisode(episode.id, options.deleteFiles, options.deleteUserData),
    onSuccess: (result) => {
      setDeleteOpen(false);
      afterDelete(result, label);
    },
    onError: (error) => toast.error(errorMessage(error)),
  });

  const deepLink = infuseDeepLink(
    { kind: "episode", seriesTmdbId: episode.seriesTmdbId, season: episode.seasonNumber, episode: episode.episodeNumber },
    { play: true },
  );

  return (
    <li className="flex flex-col">
      <div className="flex items-center gap-3 p-3 text-sm">
        <button
          onClick={() => played.mutate(!isPlayed)}
          disabled={played.isPending}
          aria-label={isPlayed ? "Mark unwatched" : "Mark watched"}
          className={cn(
            "flex size-6 shrink-0 items-center justify-center rounded-full border transition-colors",
            isPlayed ? "bg-brand text-brand-foreground border-brand" : "text-muted-foreground hover:text-foreground",
          )}
        >
          <Check className="size-3.5" aria-hidden />
        </button>
        <div className="min-w-0 flex-1">
          <p className="truncate">
            <span className="text-muted-foreground font-mono text-xs">{label}</span> {episode.title}
          </p>
          {/* What is on disk, at a glance: the default version's picture and size, and how many versions
              there are. The full surface — every version, track and control — is one click down. */}
          <p className="text-muted-foreground mt-0.5 truncate font-mono text-xs">{episodeMediaLine(episode.media)}</p>
          {resume != null && (
            <span className="bg-secondary mt-1 block h-1 max-w-32 overflow-hidden rounded-full">
              <span className="bg-brand block h-full" style={{ width: `${resume}%` }} />
            </span>
          )}
        </div>
        {runtime && <span className="text-muted-foreground shrink-0 text-xs">{runtime}</span>}
        {deepLink && (
          <Button variant="ghost" size="icon-sm" aria-label="Play in Infuse" onClick={() => openInfuse(deepLink)}>
            <Play />
          </Button>
        )}
        {role === "admin" && (
          <Button variant="ghost" size="icon-sm" aria-label="Fix match" onClick={() => setRemapOpen(true)}>
            <Wand2 />
          </Button>
        )}
        {role === "admin" && (
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label={`Delete ${label}`}
            disabled={remove.isPending}
            onClick={() => setDeleteOpen(true)}
          >
            <Trash2 />
          </Button>
        )}
        {role === "admin" && (
          <DeleteItemDialog
            open={deleteOpen}
            onOpenChange={setDeleteOpen}
            heading="Delete episode?"
            title={`${label} · ${episode.title}`}
            onConfirm={(options) => remove.mutate(options)}
          />
        )}
        {role === "admin" && (
          <RemapDialog
            itemId={episode.id}
            mode="episode"
            currentTitle={`${label} · ${episode.title}`}
            defaultSeason={episode.seasonNumber ?? 1}
            defaultEpisode={episode.episodeNumber ?? 1}
            open={remapOpen}
            onOpenChange={setRemapOpen}
            onRemapped={() => {
              setRemapOpen(false);
              invalidate();
            }}
          />
        )}
        <Button
          variant="ghost"
          size="icon-sm"
          aria-label={expanded ? `Hide media of ${label}` : `Show media of ${label}`}
          aria-expanded={expanded}
          onClick={() => setExpanded((value) => !value)}
        >
          <ChevronDown className={cn("transition-transform", !expanded && "-rotate-90")} />
        </Button>
      </div>
      {expanded && (
        <div className="border-t p-3">
          <EpisodeMedia episode={episode} seriesId={seriesId} moving={moving} />
        </div>
      )}
    </li>
  );
}

// The expanded row: the episode's versions on the same surface a movie's Media tab uses. Fetched on
// expand, so a long season never carries every episode's stream list in one listing.
function EpisodeMedia({ episode, seriesId, moving }: { episode: Episode; seriesId: string; moving: boolean }) {
  const invalidate = useEpisodeInvalidation(seriesId);
  const detail = useQuery({ queryKey: ["library-detail", episode.id], queryFn: () => mediaServer.getLibraryDetail(episode.id) });

  if (detail.isPending) {
    return <p className="text-muted-foreground text-sm">Loading media…</p>;
  }

  if (detail.isError || !detail.data) {
    return <p className="text-muted-foreground text-sm">This episode’s media could not be loaded.</p>;
  }

  return (
    <MediaSources
      owner={{ id: episode.id, kind: "Episode", title: episode.title }}
      sources={detail.data.mediaSources}
      defaultSourceId={detail.data.defaultSourceId}
      moving={moving}
      showConversions={false}
      onChanged={invalidate}
    />
  );
}
