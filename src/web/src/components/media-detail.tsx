"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useId, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Check, MoreVertical, Play, RefreshCw, Star, Trash2 } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type Episode, type LibraryDetail, type LibraryMediaSource } from "@/lib/media-server";
import { formatBytes, formatRuntime } from "@/lib/format";
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
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { cn } from "@/lib/utils";
import { useSession } from "@/components/app-shell";

/** Movie or series detail page. Branches on `kind`: movies show media streams, series show episodes. */
export function MediaDetail({ id, backHref, backLabel }: { id: string; backHref: string; backLabel: string }) {
  const detail = useQuery({ queryKey: ["library-detail", id], queryFn: () => mediaServer.getLibraryDetail(id) });

  if (detail.isPending) {
    return <p className="text-muted-foreground text-sm">Loading…</p>;
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
        <AdminControls id={item.id} title={item.title} backHref={backHref} />
      </div>
      <Hero item={item} />
      {item.overview && <p className="max-w-2xl text-sm leading-relaxed">{item.overview}</p>}
      {item.kind === "Series" ? <SeriesEpisodes seriesId={item.id} /> : <MediaInfo sources={item.mediaSources} />}
    </div>
  );
}

function AdminControls({ id, title, backHref }: { id: string; title: string; backHref: string }) {
  const { role } = useSession();
  const router = useRouter();
  const queryClient = useQueryClient();
  const [confirmOpen, setConfirmOpen] = useState(false);

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
          <DropdownMenuItem variant="destructive" onClick={() => setConfirmOpen(true)}>
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
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Delete item?</DialogTitle>
          <DialogDescription>
            Remove <span className="text-foreground font-medium">{title}</span> from the library.
          </DialogDescription>
        </DialogHeader>

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

        <DialogFooter>
          <Button type="button" variant="ghost" size="sm" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button type="button" variant="destructive" size="sm" onClick={() => onConfirm(deleteFiles)}>
            {deleteFiles ? "Delete + remove files" : "Remove from library"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
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
  const meta = [item.year?.toString(), runtime, item.genres.slice(0, 3).join(", ") || null].filter(Boolean).join(" · ");

  return (
    <div className="relative overflow-hidden rounded-lg border">
      {item.backdropUrl && (
        // eslint-disable-next-line @next/next/no-img-element
        <img src={item.backdropUrl} alt="" className="absolute inset-0 h-full w-full object-cover opacity-25" />
      )}
      <div className="from-background via-background/85 relative flex flex-col gap-4 bg-linear-to-t to-transparent p-5 sm:flex-row sm:p-6">
        <div className="bg-secondary aspect-[2/3] w-28 shrink-0 overflow-hidden rounded-md sm:w-36">
          {item.posterUrl && (
            // eslint-disable-next-line @next/next/no-img-element
            <img src={item.posterUrl} alt={item.title} className="h-full w-full object-cover" />
          )}
        </div>
        <div className="flex flex-col gap-3">
          <div className="flex flex-col gap-1">
            <h1 className="font-serif text-3xl leading-tight font-medium sm:text-4xl">{item.title}</h1>
            {meta && <p className="text-muted-foreground text-sm">{meta}</p>}
            {item.communityRating != null && (
              <p className="text-muted-foreground flex items-center gap-1 text-sm">
                <Star className="text-brand size-4" aria-hidden /> {item.communityRating.toFixed(1)}
              </p>
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
            <Button onClick={() => window.open("infuse://", "_blank")}>
              <Play className="size-4" aria-hidden /> Play in Infuse
            </Button>
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
          </div>
        </div>
      </div>
    </div>
  );
}

function MediaInfo({ sources }: { sources: LibraryMediaSource[] }) {
  if (!sources.length) {
    return null;
  }

  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold tracking-tight">Media</h2>
      {sources.map((source) => (
        <div key={source.id} className="rounded-md border p-3 text-sm">
          <p className="text-muted-foreground font-mono text-xs">
            {source.container} · {formatBytes(source.sizeBytes)}
          </p>
          <ul className="mt-2 flex flex-col gap-1">
            {source.streams.map((stream) => (
              <li key={stream.index} className="flex items-center gap-2">
                <span className="text-muted-foreground w-16 shrink-0 text-xs">{stream.type}</span>
                <span>{stream.displayTitle ?? stream.codec ?? "—"}</span>
              </li>
            ))}
          </ul>
        </div>
      ))}
    </section>
  );
}

function SeriesEpisodes({ seriesId }: { seriesId: string }) {
  const episodes = useQuery({ queryKey: ["episodes", seriesId], queryFn: () => mediaServer.listEpisodes(seriesId) });

  if (episodes.isPending) {
    return <p className="text-muted-foreground text-sm">Loading episodes…</p>;
  }

  const all = episodes.data ?? [];
  if (!all.length) {
    return null;
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

function EpisodeRow({ episode, seriesId }: { episode: Episode; seriesId: string }) {
  const queryClient = useQueryClient();
  const played = useMutation({
    mutationFn: (value: boolean) => mediaServer.setPlayed(episode.id, value),
    onSuccess: () => {
      for (const key of [["episodes", seriesId], ["library-detail", seriesId], ["nextup"], ["resume"]]) {
        queryClient.invalidateQueries({ queryKey: key });
      }
    },
  });

  const isPlayed = episode.userData?.played ?? false;
  const resume = !isPlayed && episode.userData?.playedPercentage ? Math.min(episode.userData.playedPercentage, 100) : null;
  const runtime = formatRuntime(episode.runtimeTicks);
  const label = `S${String(episode.seasonNumber ?? 0).padStart(2, "0")}E${String(episode.episodeNumber ?? 0).padStart(2, "0")}`;

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
    </li>
  );
}
