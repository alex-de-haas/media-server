"use client";

import Link from "next/link";
import { EyeOff, Plus, Sparkles } from "lucide-react";
import { cn } from "@/lib/utils";
import type { Recommendation } from "@/lib/media-server";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";

/**
 * One recommended title. A held title links to its detail page; a discovery opens the Track flow,
 * because this surface never pretends playback is available for something the instance lacks.
 */
export function RecommendationCard({
  item,
  onHide,
  onTrack,
}: {
  item: Recommendation;
  onHide: (item: Recommendation) => void;
  onTrack: (item: Recommendation) => void;
}) {
  // The media-item id, not the public id: the detail routes are declared `{id:guid}` and resolve by
  // MediaItem.Id, so a public id — a deterministic hash — would not even match the route.
  const href = item.inLibrary && item.mediaItemId
    ? `/${item.kind === "Series" ? "series" : "movies"}/${item.mediaItemId}`
    : null;
  // Independent engines agreeing is the strongest signal the feed has, so it is worth saying out loud.
  const agreed = item.sources.length > 1;

  const poster = (
    <span className="bg-secondary relative block aspect-[2/3] w-full overflow-hidden rounded-md">
      {item.posterUrl ? (
        // eslint-disable-next-line @next/next/no-img-element
        <img src={item.posterUrl} alt="" className="h-full w-full object-cover" loading="lazy" />
      ) : (
        <span className="text-muted-foreground flex h-full w-full items-center justify-center text-xs">
          No poster
        </span>
      )}
      {agreed && (
        <Badge className="absolute top-1 left-1 gap-1 px-1.5 py-0" variant="secondary" title="Both sources suggested this">
          <Sparkles className="size-3" aria-hidden /> Both
        </Badge>
      )}
    </span>
  );

  return (
    <div className="group/rec relative flex flex-col gap-1.5">
      {href ? (
        <Link href={href} className="block">
          {poster}
        </Link>
      ) : (
        poster
      )}

      <div className="flex flex-col gap-0.5">
        <span className="truncate text-sm font-medium" title={item.title}>
          {item.title}
        </span>
        <span className="text-muted-foreground flex items-center gap-1.5 text-xs">
          {item.year ?? "—"}
          <span
            data-testid="rec-availability"
            className={cn(item.inLibrary ? "text-brand" : "text-muted-foreground")}
          >
            {item.inLibrary ? "In library" : "Not in library"}
          </span>
        </span>
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
