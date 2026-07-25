"use client";

import { useRef } from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { Clapperboard, ExternalLink, EyeOff, Library, Star, User, X } from "lucide-react";
import { mediaServer, type CastMember, type TitlePreview } from "@/lib/media-server";
import { formatRuntime } from "@/lib/format";
import { formatCount, openExternal } from "@/lib/ui";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { ErrorState } from "@/components/states";
import { TrackTitleControl } from "@/components/track-title-control";

/** What the caller already knows about the title — enough to render the dialog before the fetch lands. */
export interface TitlePreviewTarget {
  provider: string;
  providerId: string;
  kind: "Movie" | "Series";
  title: string;
  year?: number | null;
  posterUrl?: string | null;
}

/**
 * What a title *is*, for a title this instance may not hold: the preview behind a recommendation, a
 * tracked row, a calendar entry or a search result. Playback, versions and files are deliberately absent —
 * a held title has a full detail page, and this dialog links to it instead of imitating it.
 */
export function TitlePreviewDialog({
  target,
  onOpenChange,
  onHide,
}: {
  // Null closes the dialog; the caller keeps the target so it can reopen with a different title.
  target: TitlePreviewTarget | null;
  onOpenChange: (open: boolean) => void;
  // Only the recommendation surfaces can dismiss a title, so the action shows only when they pass this.
  onHide?: (target: TitlePreviewTarget) => void;
}) {
  const closeRef = useRef<HTMLButtonElement>(null);

  const preview = useQuery({
    queryKey: ["title-preview", target?.provider, target?.providerId, target?.kind],
    queryFn: () => mediaServer.getTitlePreview(target!.provider, target!.providerId, target!.kind),
    enabled: target !== null,
    // The facts of a title do not move while a dialog is open, and reopening one should feel instant.
    staleTime: 30 * 60_000,
  });

  const detail = preview.data;
  // Until the fetch lands, the card's own poster/title/year stand in — the dialog opens on what is known.
  const title = detail?.title ?? target?.title ?? "";
  const year = detail?.year ?? target?.year ?? null;
  const posterUrl = detail?.posterUrl ?? target?.posterUrl ?? null;

  return (
    <Dialog open={target !== null} onOpenChange={onOpenChange}>
      {/* Focus has to land inside the preview: opened over the tracked drawer it is a sibling of that
          drawer, whose own focus trap otherwise keeps the caret — and Escape would then close the drawer
          underneath instead of the dialog on top. */}
      <DialogContent
        className="max-h-[85vh] gap-0 overflow-hidden p-0 sm:max-w-2xl"
        showCloseButton={false}
        initialFocus={closeRef}
      >
        {/* The shared close button is a bare glyph, which disappears against a bright backdrop; this one
            carries its own scrim so it stays legible over any artwork. */}
        <DialogClose
          ref={closeRef}
          aria-label="Close"
          className="absolute top-3 right-3 z-10 rounded-full bg-black/40 p-1.5 text-white/90 transition-colors hover:bg-black/60 hover:text-white focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
        >
          <X className="size-4" />
        </DialogClose>

        {/* Banner and body scroll together, so the poster keeps its overlap however far the text runs. */}
        <div className="flex min-h-0 flex-col overflow-y-auto">
          <Banner backdropUrl={detail?.backdropUrl ?? null} />

          <div className="relative -mt-16 flex flex-col gap-4 p-6">
          <div className="flex gap-4">
            <div className="bg-secondary aspect-[2/3] w-24 shrink-0 overflow-hidden rounded-md shadow-lg ring-1 ring-black/10 sm:w-28">
              {posterUrl && (
                // eslint-disable-next-line @next/next/no-img-element
                <img src={posterUrl} alt="" className="h-full w-full object-cover" />
              )}
            </div>

            <DialogHeader className="min-w-0 flex-1 gap-1.5 text-left">
              <DialogTitle className="font-serif text-2xl leading-tight font-medium">{title}</DialogTitle>
              {/* The description renders a <p>, so the pending placeholder stays outside it — a div in a
                  paragraph is invalid HTML and React rejects the hydration. */}
              {preview.isPending ? (
                <Skeleton className="h-4 w-48" />
              ) : (
                <DialogDescription>
                  <Facts item={detail} year={year} />
                </DialogDescription>
              )}
              {detail && <Ratings item={detail} />}
              {detail && <CreditLine item={detail} />}
            </DialogHeader>
          </div>

          {preview.isError ? (
            <ErrorState onRetry={() => void preview.refetch()} />
          ) : preview.isPending ? (
            <div className="flex flex-col gap-2">
              <Skeleton className="h-4 w-full" />
              <Skeleton className="h-4 w-11/12" />
              <Skeleton className="h-4 w-2/3" />
            </div>
          ) : (
            detail && (
              <>
                {detail.tagline && <p className="text-muted-foreground text-sm italic">{detail.tagline}</p>}
                {detail.overview && <p className="text-sm leading-relaxed">{detail.overview}</p>}
                <Cast cast={detail.cast} />
              </>
            )
          )}

          <div className="flex flex-wrap items-center gap-2 pt-1">
            {detail?.inLibrary && detail.mediaItemId && (
              <Button render={<Link href={`/${detail.kind === "Series" ? "series" : "movies"}/${detail.mediaItemId}`} />}>
                <Library className="size-4" aria-hidden /> Open in library
              </Button>
            )}
            {target && (
              <TrackTitleControl
                tmdbId={target.providerId}
                kind={target.kind}
                title={title}
                year={year}
                posterUrl={posterUrl}
              />
            )}
            {detail?.trailerUrl && (
              <Button variant="outline" onClick={() => openExternal(detail.trailerUrl!)}>
                <Clapperboard className="size-4" aria-hidden /> Trailer
              </Button>
            )}
            {detail?.imdbId && (
              <Button
                variant="secondary"
                aria-label="View on IMDb"
                className="border-transparent bg-[#f5c518] text-black hover:bg-[#e4b915] hover:text-black"
                onClick={() => openExternal(`https://www.imdb.com/title/${detail.imdbId}/`)}
              >
                <span className="font-semibold tracking-normal">IMDb</span>
                <ExternalLink className="size-4" aria-hidden />
              </Button>
            )}
            {onHide && target && !detail?.inLibrary && (
              <Button
                variant="ghost"
                className="ml-auto"
                onClick={() => {
                  onHide(target);
                  onOpenChange(false);
                }}
              >
                <EyeOff className="size-4" aria-hidden /> Not interested
              </Button>
            )}
          </div>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

// The backdrop band the poster overlaps, scrimmed downward so the text below it stays legible. Without
// artwork it collapses to a plain tinted strip rather than a hole in the layout.
function Banner({ backdropUrl }: { backdropUrl: string | null }) {
  return (
    <div className="bg-secondary relative h-32 w-full shrink-0 overflow-hidden sm:h-40">
      {backdropUrl && (
        // eslint-disable-next-line @next/next/no-img-element
        <img src={backdropUrl} alt="" className="h-full w-full object-cover opacity-70" />
      )}
      <div className="from-popover absolute inset-0 bg-linear-to-t to-transparent" />
    </div>
  );
}

function Facts({ item, year }: { item: TitlePreview | undefined; year: number | null }) {
  const seasons =
    item?.kind === "Series" && item.seasonCount
      ? `${item.seasonCount} season${item.seasonCount === 1 ? "" : "s"}`
      : null;
  const facts = [
    year?.toString() ?? null,
    item ? formatRuntime(item.runtimeTicks) : null,
    seasons,
    item?.genres.slice(0, 3).join(", ") || null,
  ].filter(Boolean);

  return <>{facts.join(" · ")}</>;
}

function Ratings({ item }: { item: TitlePreview }) {
  if (!item.officialRating && item.communityRating == null && !item.status) {
    return null;
  }

  return (
    <span className="flex flex-wrap items-center gap-x-3 gap-y-1">
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
    </span>
  );
}

function CreditLine({ item }: { item: TitlePreview }) {
  const names = item.kind === "Series" ? item.creators : item.directors;
  if (names.length === 0) {
    return null;
  }

  return (
    <span>
      {item.kind === "Series" ? "Created by " : "Directed by "}
      <span className="text-foreground">{names.slice(0, 3).join(", ")}</span>
    </span>
  );
}

// A scrolling strip of the billed cast. Names do not link: for a title nobody holds there is no person
// page to link to — the people exist here only as the provider's credits.
function Cast({ cast }: { cast: CastMember[] }) {
  if (cast.length === 0) {
    return null;
  }

  return (
    <section className="flex flex-col gap-2">
      <h3 className="text-muted-foreground text-xs font-medium tracking-wide uppercase">Cast</h3>
      <ul className="-mx-1 flex gap-3 overflow-x-auto px-1 pb-1">
        {cast.slice(0, 12).map((member) => (
          <li key={`${member.providerId}:${member.character ?? ""}`} className="w-20 shrink-0">
            <div className="bg-secondary aspect-[2/3] w-full overflow-hidden rounded-md ring-1 ring-black/5">
              {member.profileUrl ? (
                // eslint-disable-next-line @next/next/no-img-element
                <img src={member.profileUrl} alt="" className="h-full w-full object-cover" loading="lazy" />
              ) : (
                <div className="text-muted-foreground flex h-full w-full items-center justify-center">
                  <User className="size-6" aria-hidden />
                </div>
              )}
            </div>
            <p className="mt-1 truncate text-xs font-medium" title={member.name}>
              {member.name}
            </p>
            {member.character && (
              <p className="text-muted-foreground truncate text-xs" title={member.character}>
                {member.character}
              </p>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}
