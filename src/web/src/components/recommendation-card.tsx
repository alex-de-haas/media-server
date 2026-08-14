"use client";

import Link from "next/link";
import { Check, EyeOff, Plus } from "lucide-react";
import { reasonText, type Recommendation } from "@/lib/media-server";
import { Button } from "@/components/ui/button";

/**
 * One recommended title. A held title links to its detail page; a discovery opens the preview dialog,
 * whose actions are Track and Not interested — this surface never pretends playback is available for
 * something the instance lacks.
 */
export function RecommendationCard({
  item,
  onHide,
  onTrack,
  onOpen,
  showReason = false,
}: {
  item: Recommendation;
  onHide: (item: Recommendation) => void;
  onTrack: (item: Recommendation) => void;
  onOpen: (item: Recommendation) => void;
  /** Render the reason as a third line. The Home row leaves it off and keeps the tooltip instead. */
  showReason?: boolean;
}) {
  const reason = reasonText(item.reason);
  // The media-item id, not the public id: the detail routes are declared `{id:guid}` and resolve by
  // MediaItem.Id, so a public id — a deterministic hash — would not even match the route.
  const href = item.inLibrary && item.mediaItemId
    ? `/${item.kind === "Series" ? "series" : "movies"}/${item.mediaItemId}`
    : null;

  const poster = (
    <span className="bg-secondary relative block aspect-[2/3] w-full overflow-hidden rounded-md">
      {item.posterUrl ? (
        // eslint-disable-next-line @next/next/no-img-element
        <img src={item.posterUrl} alt="" className="h-full w-full object-cover" loading="lazy" />
      ) : (
        <span className="text-muted-foreground flex h-full w-full items-center justify-center p-2 text-center text-xs">
          No poster
        </span>
      )}
    </span>
  );

  return (
    <div className="group/rec relative flex flex-col gap-1.5">
      {href ? (
        // The poster is decorative (alt="") and the title lives below the card, so the link needs a label
        // of its own — without one a screen reader announces an unnamed link.
        <Link href={href} aria-label={`Open ${item.title}`} className="block">
          {poster}
        </Link>
      ) : (
        // A discovery has no page to link to, so the poster opens the preview instead of going nowhere.
        // The label is an aria-label rather than visually-hidden text: the title already appears below the
        // poster, and a second copy in the DOM would read as a duplicate.
        <button
          type="button"
          aria-label={`Details for ${item.title}`}
          className="block cursor-pointer text-left"
          onClick={() => onOpen(item)}
        >
          {poster}
        </button>
      )}

      {/* Same two lines as an ordinary poster card — 13px name over a 12px muted caption — so a
          recommendation sitting next to a library tile on Home reads at the same weight. */}
      <div className="flex min-w-0 flex-col gap-0.5">
        <span className="truncate text-[13px] font-medium" title={reason ? `${item.title} — ${reason}` : item.title}>
          {item.title}
        </span>
        {/* The same `kind · year` caption an ordinary poster card carries. Availability is the amber check
            the tracked drawer and the calendar already use for "you have this" — only held titles are
            marked, since saying "not in library" on almost every card spends the line to say nothing. */}
        <span className="text-muted-foreground flex items-center gap-1 text-xs">
          <span className="truncate">
            {item.kind}
            {item.year ? ` · ${item.year}` : ""}
          </span>
          {item.inLibrary && (
            <Check data-testid="rec-availability" className="text-brand size-3.5 shrink-0" aria-label="In library" />
          )}
        </span>
        {/* A third line is one more than the two this tile was deliberately matched to, so it appears
            only where there is room for it — the grid on the recommendations page. In the Home row it
            stays a tooltip, which costs no height and is still there for anyone who wonders. */}
        {reason && showReason && (
          <span className="text-muted-foreground/80 truncate text-[11px]" data-testid="rec-reason">
            {reason}
          </span>
        )}
      </div>

      <div className="flex items-center gap-1">
        {!item.inLibrary && (
          <Button variant="secondary" size="sm" className="h-7 flex-1 text-xs" onClick={() => onTrack(item)}>
            <Plus className="size-3.5" aria-hidden /> Track
          </Button>
        )}
        <Button
          variant="ghost"
          size="icon-sm"
          aria-label={`Hide ${item.title}`}
          title="Not interested"
          onClick={() => onHide(item)}
        >
          <EyeOff />
        </Button>
      </div>
    </div>
  );
}
