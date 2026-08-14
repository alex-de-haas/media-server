"use client";

import { useState } from "react";
import { Star } from "lucide-react";
import { cn } from "@/lib/utils";

/**
 * The user's own 1-5 star verdict on a watched work.
 *
 * A separate gesture from Favorite, which is curation ("keep this where I can find it") — this one
 * answers "was it any good", and the recommendation engine weighs the two ends of the scale very
 * differently. Clicking the lit star clears the rating back to *unrated*, which is a different
 * statement from one star and so needs a way back that is not "rate it badly".
 */
export function StarRating({
  value,
  onChange,
  pending = false,
}: {
  value: number | null;
  onChange: (next: number | null) => void;
  pending?: boolean;
}) {
  const [hover, setHover] = useState<number | null>(null);
  // The hover preview shows what a click would leave behind, so hovering the lit star shows the
  // clear rather than lighting it again.
  const shown = hover ?? value ?? 0;

  return (
    <div
      className="border-border bg-background dark:border-input dark:bg-input/30 inline-flex h-8 items-center gap-0.5 rounded-lg border px-1.5"
      onMouseLeave={() => setHover(null)}
      role="group"
      aria-label="Your rating"
    >
      {[1, 2, 3, 4, 5].map((star) => {
        const clears = value === star;
        return (
          <button
            key={star}
            type="button"
            disabled={pending}
            onClick={() => onChange(clears ? null : star)}
            onMouseEnter={() => setHover(clears ? star - 1 : star)}
            onFocus={() => setHover(clears ? star - 1 : star)}
            onBlur={() => setHover(null)}
            aria-label={clears ? "Clear your rating" : `Rate ${star} star${star === 1 ? "" : "s"}`}
            aria-pressed={value !== null && star <= value}
            className="focus-visible:ring-ring/50 rounded-sm p-0.5 outline-none focus-visible:ring-3 disabled:pointer-events-none disabled:opacity-50"
          >
            <Star
              className={cn(
                "size-4 transition-colors",
                star <= shown ? "text-brand fill-brand" : "text-muted-foreground",
              )}
              aria-hidden
            />
          </button>
        );
      })}
    </div>
  );
}
