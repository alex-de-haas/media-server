"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useId, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Check, ChevronDown, Clapperboard, ExternalLink, FolderInput, Link2, MoreVertical, Pencil, Play, RefreshCw, Shrink, Star, Trash2, User, Wand2 } from "lucide-react";
import { toast } from "@/lib/toast";
import {
  mediaServer,
  type CastMember,
  type Episode,
  type LibraryDetail,
  type LibraryMediaSource,
  type LibraryMoveJob,
  type MediaStream,
  type Network,
  type Studio,
  type TranscodeJob,
} from "@/lib/media-server";
import { TranscodeDialog, TranscodeJobRow, isTranscodeActive } from "@/components/transcode";
import { infuseDeepLink, openInfuse } from "@/lib/infuse";
import { personHref } from "@/components/poster-card";
import { RemapDialog } from "@/components/remap-dialog";
import { MoveToCatalogDialog } from "@/components/move-to-catalog-dialog";
import { formatBytes, formatEta, formatRuntime, formatSpeed } from "@/lib/format";
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
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Progress } from "@/components/ui/progress";
import { Separator } from "@/components/ui/separator";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { cn } from "@/lib/utils";
import { useSession } from "@/components/app-shell";

/** Movie or series detail page. Branches on `kind`: movies show media streams, series show episodes. */
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
        <AdminControls id={item.id} title={item.title} kind={item.kind} catalogId={item.catalogId} backHref={backHref} />
      </div>
      <Hero item={item} />
      <DetailTabs item={item} />
    </div>
  );
}

function DetailTabs({ item }: { item: LibraryDetail }) {
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
          {item.kind === "Series" ? <SeriesEpisodes seriesId={item.id} /> : <MediaInfo item={item} />}
        </div>
      </TabsContent>
      <TabsContent value="tags">
        <KeywordTags keywords={item.keywords} />
      </TabsContent>
    </Tabs>
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
      <div className="flex items-center gap-2">
        <Progress value={move.progress} className="h-1.5" />
        <span className="text-muted-foreground shrink-0 font-mono text-xs tabular-nums">{move.progress}%</span>
      </div>
      {(move.bytesPerSecond != null || move.etaSeconds != null) && (
        <div className="text-muted-foreground flex flex-wrap gap-x-3 font-mono text-xs tabular-nums">
          <span>{formatSpeed(move.bytesPerSecond)}</span>
          <span>ETA {formatEta(move.etaSeconds)}</span>
        </div>
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

function AdminControls({ id, title, kind, catalogId, backHref }: { id: string; title: string; kind: string; catalogId: string; backHref: string }) {
  const { role } = useSession();
  const router = useRouter();
  const queryClient = useQueryClient();
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [remapOpen, setRemapOpen] = useState(false);
  const [moveOpen, setMoveOpen] = useState(false);
  // While a move is relocating this item's files, everything that mutates the item or reads its files is
  // locked (the API rejects it with a 409 anyway) — only the provider-side metadata refresh stays usable.
  const moving = useActiveMove(id) !== undefined;

  const remove = useMutation({
    mutationFn: (deleteFiles: boolean) => mediaServer.deleteLibraryItem(id, deleteFiles),
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
  // languages, track titles) that wasn't captured at import time, without a full library rescan.
  const refreshMedia = useMutation({
    mutationFn: () => mediaServer.refreshMedia(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["library-detail", id] });
      toast.success("Media data refreshed");
    },
    onError: (error) => toast.error("Couldn’t refresh media data", { description: errorMessage(error) }),
  });

  if (role !== "admin") {
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
          <DropdownMenuItem disabled={refresh.isPending} onClick={() => refresh.mutate()}>
            <RefreshCw className={cn(refresh.isPending && "animate-spin")} aria-hidden />
            Refresh metadata
          </DropdownMenuItem>
          {/* Media sources live on the leaf (movie/episode), not the series row. */}
          {kind !== "Series" && (
            <DropdownMenuItem disabled={refreshMedia.isPending || moving} onClick={() => refreshMedia.mutate()}>
              <Clapperboard className={cn(refreshMedia.isPending && "animate-pulse")} aria-hidden />
              Refresh media data
            </DropdownMenuItem>
          )}
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
        </DropdownMenuContent>
      </DropdownMenu>

      <DeleteItemDialog
        open={confirmOpen}
        onOpenChange={setConfirmOpen}
        title={title}
        onConfirm={(deleteFiles) => {
          remove.mutate(deleteFiles);
          setConfirmOpen(false);
        }}
      />

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
  );
}

function DeleteItemDialog({
  open,
  onOpenChange,
  title,
  onConfirm,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  onConfirm: (deleteFiles: boolean) => void;
}) {
  // Default to keeping files: deleting a published item shouldn't silently remove the media on disk.
  const [deleteFiles, setDeleteFiles] = useState(false);
  const deleteFilesId = useId();

  // Re-apply the default every time the dialog (re)opens so a prior toggle (then cancel) doesn't carry over.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) setDeleteFiles(false);
  }

  return (
    <AlertDialog open={open} onOpenChange={onOpenChange}>
      <AlertDialogContent className="sm:max-w-md">
        <AlertDialogHeader>
          <AlertDialogTitle>Delete item?</AlertDialogTitle>
          <AlertDialogDescription>
            Remove <span className="text-foreground font-medium">{title}</span> from the library.
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

        <AlertDialogFooter>
          <AlertDialogCancel size="sm">Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" size="sm" onClick={() => onConfirm(deleteFiles)}>
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

  const isPlayed = item.userData?.played ?? false;
  const isFavorite = item.userData?.isFavorite ?? false;
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
                <h1 className="font-serif text-3xl leading-tight font-medium sm:text-4xl">{item.title}</h1>
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
                <Star className="size-4" aria-hidden /> Favorite
              </Button>
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
function openExternal(url: string) {
  const opened = window.open(url, "_blank", "noopener,noreferrer");
  if (opened) {
    opened.opener = null;
  }
}

// Compact vote counts: 12345 → "12K". Pin the locale so SSR and the client format identically (avoids a
// hydration mismatch); the UI is English-only.
const countFormatter = new Intl.NumberFormat("en", { notation: "compact", maximumFractionDigits: 1 });
function formatCount(value: number) {
  return countFormatter.format(value);
}

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

function MediaInfo({ item }: { item: LibraryDetail }) {
  const { role } = useSession();
  const moving = useActiveMove(item.id) !== undefined;
  // Transcoding ("shrink a movie source into a smaller version") is movies-only and admin-only for now.
  // Source management (convert/rename/pin/delete) is locked while a move is relocating these very files —
  // the API rejects those calls with a 409, so don't offer them (the MoveProgress bar above explains why).
  const admin = item.kind === "Movie" && role === "admin";
  const canManage = admin && !moving;
  const sources = item.mediaSources;

  if (!sources.length && !canManage) {
    return <EmptyDetailPanel>No media sources available.</EmptyDetailPanel>;
  }

  return (
    <section className="flex flex-col gap-3">
      {admin && <MovieConversions itemId={item.id} />}
      {sources.length ? (
        sources.map((source) => (
          <SourceCard
            key={source.id}
            source={source}
            itemId={item.id}
            title={item.title}
            year={item.year}
            canManage={canManage}
            isDefault={source.id === item.defaultSourceId}
            hasMultiple={sources.length > 1}
          />
        ))
      ) : (
        <EmptyDetailPanel>No media sources available.</EmptyDetailPanel>
      )}
    </section>
  );
}

// Stream types we know how to order/label; anything else falls through in its original order with its raw
// type name. "Subtitle" reads better pluralised once it heads a group of them.
const STREAM_TYPE_ORDER = ["Video", "Audio", "Subtitle"];
const STREAM_TYPE_LABELS: Record<string, string> = { Subtitle: "Subtitles" };

type StreamGroup = { type: string; label: string; streams: MediaStream[]; defaultIndex: number | null };

// Group the flat stream list by type, preserving each individual track (no dedup) and container order.
// `defaultIndex` is the track a player treats as default — an explicitly flagged track, or (audio only) the
// first track when none is flagged; subtitles stay off unless flagged. Matches the Convert dialog.
function groupStreams(streams: MediaStream[]): StreamGroup[] {
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
    .map((type) => {
      const list = byType.get(type)!;
      const flagged = list.find((stream) => stream.isDefault)?.index;
      const defaultIndex = flagged ?? (type === "Audio" ? list[0]?.index : undefined) ?? null;
      return { type, label: STREAM_TYPE_LABELS[type] ?? type, streams: list, defaultIndex };
    });
}

// Secondary technical specs shown muted after a track: video → profile · bit depth · frame rate; audio →
// sample rate. Only what the probe captured; "" when nothing to add. Numbers are trimmed of trailing zeros.
function streamSpecs(stream: MediaStream): string {
  const parts: string[] = [];
  if (stream.type === "Video") {
    if (stream.profile) parts.push(stream.profile);
    if (stream.bitDepth) parts.push(`${stream.bitDepth}-bit`);
    if (stream.frameRate) parts.push(`${Number(stream.frameRate.toFixed(3))} fps`);
  } else if (stream.type === "Audio") {
    if (stream.sampleRate) parts.push(`${Number((stream.sampleRate / 1000).toFixed(1))} kHz`);
  }
  return parts.join(" · ");
}

// The per-track summary text, its optional container label ("Director's Commentary", "SDH") — dropping a
// label that just restates the summary — and the muted secondary specs.
function TrackText({ stream }: { stream: MediaStream }) {
  const text = stream.displayTitle ?? stream.codec ?? "—";
  const raw = stream.title?.trim();
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

// One "Video/Audio/Subtitles" section. A single track shows inline; a section with several collapses into a
// toggle ("N tracks") that expands to list every track, marking the default one.
function StreamSection({ group }: { group: StreamGroup }) {
  const [open, setOpen] = useState(false);

  if (group.streams.length <= 1) {
    const stream = group.streams[0];
    return (
      <div className="flex gap-2">
        <dt className="text-muted-foreground w-16 shrink-0 pt-px text-xs leading-5">{group.label}</dt>
        <dd className="leading-5">{stream ? <TrackText stream={stream} /> : "—"}</dd>
      </div>
    );
  }

  return (
    <div className="flex gap-2">
      <dt className="text-muted-foreground w-16 shrink-0 pt-px text-xs leading-5">{group.label}</dt>
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

// One source/version card. The file name (title + year part) is read-only; the editable label is the
// version (shown in players' version pickers) and an admin can pin which version plays by default. For
// movies an admin can also convert it into a smaller version or delete it (the "verify then replace"
// flow: convert → check the new version → delete the original).
function SourceCard({
  source,
  itemId,
  title,
  year,
  canManage,
  isDefault,
  hasMultiple,
}: {
  source: LibraryMediaSource;
  itemId: string;
  title: string;
  year: number | null;
  canManage: boolean;
  isDefault: boolean;
  hasMultiple: boolean;
}) {
  const queryClient = useQueryClient();
  const [convertOpen, setConvertOpen] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [editOpen, setEditOpen] = useState(false);
  const hdr = source.streams.find((stream) => stream.type === "Video" && stream.hdrFormat)?.hdrFormat;

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
      queryClient.invalidateQueries({ queryKey: ["library-detail", itemId] });
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
            {hdr ? (
              <Badge variant="secondary" className="font-normal">
                {hdr}
              </Badge>
            ) : null}
          </div>
          <p className="text-muted-foreground mt-1 font-mono text-xs">{metaParts.join(" · ")}</p>
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
            <Button variant="ghost" size="icon-sm" aria-label="Convert to a smaller version" onClick={() => setConvertOpen(true)}>
              <Shrink />
            </Button>
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

      {canManage && (
        <EditVersionDialog
          source={source}
          itemId={itemId}
          title={title}
          year={year}
          open={editOpen}
          onOpenChange={setEditOpen}
        />
      )}
      {canManage && <TranscodeDialog source={source} open={convertOpen} onOpenChange={setConvertOpen} />}
      {canManage && <DeleteVersionDialog source={source} itemId={itemId} open={deleteOpen} onOpenChange={setDeleteOpen} />}
    </div>
  );
}

// Characters the server rejects (they'd be stripped from a filename). Mirrored here so the field flags them
// before the request, but the server stays the source of truth.
const INVALID_VERSION_CHARS = /[/\\:*?"<>|]/;

// Rename or clear a movie source's version — the ` - {version}` suffix on its filename, which also labels the
// version in players (e.g. Infuse). This renames the file on disk; the `Title (Year)` stem is locked.
function EditVersionDialog({
  source,
  itemId,
  title,
  year,
  open,
  onOpenChange,
}: {
  source: LibraryMediaSource;
  itemId: string;
  title: string;
  year: number | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const queryClient = useQueryClient();
  const inputId = useId();
  const [value, setValue] = useState(source.versionName ?? "");

  // Re-seed the field with the current version each time the dialog (re)opens.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) setValue(source.versionName ?? "");
  }

  // Preview the resulting file name: locked "Title (Year)" stem + the typed suffix + the current extension.
  // Normalize the suffix the same way the server does (collapse runs of spaces, drop trailing dots) so the
  // preview matches what actually lands on disk.
  const trimmed = value.trim();
  const suffix = trimmed.replace(/\s+/g, " ").replace(/\.+$/, "");
  const invalid = INVALID_VERSION_CHARS.test(value);
  const stem = year != null ? `${title} (${year})` : title;
  const fileName = source.fileName ?? "";
  const extension = fileName.includes(".") ? fileName.slice(fileName.lastIndexOf(".")) : "";
  const previewName = `${stem}${suffix ? ` - ${suffix}` : ""}${extension}`;

  const save = useMutation({
    mutationFn: (next: string | null) => mediaServer.setSourceVersion(source.id, next),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["library-detail", itemId] });
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

// This movie's transcode jobs, polled while any is active. When the last one finishes, the detail is
// refreshed so the freshly produced version appears in the source list above.
function MovieConversions({ itemId }: { itemId: string }) {
  const queryClient = useQueryClient();
  const jobs = useQuery({
    queryKey: ["transcode-jobs"],
    queryFn: mediaServer.listTranscodeJobs,
    refetchInterval: (query) => {
      const data = (query.state.data ?? []) as TranscodeJob[];
      return data.some((job) => job.mediaItemId === itemId && isTranscodeActive(job)) ? 2000 : false;
    },
  });

  const mine = (jobs.data ?? []).filter((job) => job.mediaItemId === itemId);
  const activeCount = mine.filter(isTranscodeActive).length;

  const previousActive = useRef(activeCount);
  useEffect(() => {
    if (previousActive.current > 0 && activeCount === 0) {
      queryClient.invalidateQueries({ queryKey: ["library-detail", itemId] });
    }
    previousActive.current = activeCount;
  }, [activeCount, itemId, queryClient]);

  if (mine.length === 0) {
    return null;
  }

  return (
    <div className="flex flex-col gap-2 rounded-md border border-dashed p-3">
      <p className="text-muted-foreground text-xs font-medium">Conversions</p>
      {mine.map((job) => (
        <TranscodeJobRow key={job.id} job={job} />
      ))}
    </div>
  );
}

function DeleteVersionDialog({
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
      queryClient.invalidateQueries({ queryKey: ["library-detail", itemId] });
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
            {formatBytes(source.sizeBytes)}) from this movie.
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

function SeriesEpisodes({ seriesId }: { seriesId: string }) {
  const episodes = useQuery({ queryKey: ["episodes", seriesId], queryFn: () => mediaServer.listEpisodes(seriesId) });

  if (episodes.isPending) {
    return <p className="text-muted-foreground text-sm">Loading episodes…</p>;
  }

  const all = episodes.data ?? [];
  if (!all.length) {
    return <EmptyDetailPanel>No episodes available.</EmptyDetailPanel>;
  }

  const seasons = new Map<number, Episode[]>();
  for (const episode of all) {
    const season = episode.seasonNumber ?? 0;
    const list = seasons.get(season) ?? [];
    list.push(episode);
    seasons.set(season, list);
  }

  return (
    <section className="flex flex-col gap-4">
      {[...seasons.entries()]
        .sort((a, b) => a[0] - b[0])
        .map(([season, eps]) => (
          <div key={season} className="flex flex-col gap-2">
            <h2 className="text-lg font-semibold tracking-tight">Season {season}</h2>
            <Separator />
            <ul className="flex flex-col divide-y rounded-md border">
              {eps.map((episode) => (
                <EpisodeRow key={episode.id} episode={episode} seriesId={seriesId} />
              ))}
            </ul>
          </div>
        ))}
    </section>
  );
}

function EmptyDetailPanel({ children }: { children: string }) {
  return <p className="text-muted-foreground py-6 text-sm">{children}</p>;
}

function EpisodeRow({ episode, seriesId }: { episode: Episode; seriesId: string }) {
  const { role } = useSession();
  const queryClient = useQueryClient();
  const [remapOpen, setRemapOpen] = useState(false);

  const invalidate = () => {
    for (const key of [["episodes", seriesId], ["library-detail", seriesId], ["library"], ["nextup"], ["resume"], ["recent"]]) {
      queryClient.invalidateQueries({ queryKey: key });
    }
  };

  const played = useMutation({
    mutationFn: (value: boolean) => mediaServer.setPlayed(episode.id, value),
    onSuccess: invalidate,
  });

  const isPlayed = episode.userData?.played ?? false;
  const resume = !isPlayed && episode.userData?.playedPercentage ? Math.min(episode.userData.playedPercentage, 100) : null;
  const runtime = formatRuntime(episode.runtimeTicks);
  const label = `S${String(episode.seasonNumber ?? 0).padStart(2, "0")}E${String(episode.episodeNumber ?? 0).padStart(2, "0")}`;
  const deepLink = infuseDeepLink(
    { kind: "episode", seriesTmdbId: episode.seriesTmdbId, season: episode.seasonNumber, episode: episode.episodeNumber },
    { play: true },
  );

  return (
    <li className="flex items-center gap-3 p-3 text-sm">
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
    </li>
  );
}
