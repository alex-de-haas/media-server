"use client";

import Link from "next/link";
import { Check } from "lucide-react";
import type { UserItemData } from "@/lib/media-server";

/** Detail-page route for a top-level item. Movies and series are separate tabs. */
export function detailHref(kind: string, id: string): string {
  return kind === "Series" ? `/series/${id}` : `/movies/${id}`;
}

// The person page is keyed by the provider identity its cast members carry, but the route is a single
// `[id]` segment — so the pair is joined as `provider-providerId` and split back on the first dash
// (provider tokens never contain one). Keep these two in sync.
export function personHref(provider: string, providerId: string): string {
  return `/people/${provider}-${providerId}`;
}

// Splits a `[id]` route segment back into the provider identity. A malformed id (no dash, or an empty
// half from a leading/trailing dash) yields a blank pair so callers can short-circuit instead of issuing
// an invalid `/persons/{provider}/` request.
export function parsePersonId(id: string): { provider: string; providerId: string } {
  const dash = id.indexOf("-");
  if (dash <= 0 || dash === id.length - 1) {
    return { provider: "", providerId: "" };
  }
  return { provider: id.slice(0, dash), providerId: id.slice(dash + 1) };
}

// A poster tile used in both the library grids and the Home rails. Title is set in the serif display
// face ("content speaks serif"); the amber accent carries the resume bar and the watched badge.
export function PosterCard({
  href,
  title,
  subtitle,
  posterUrl,
  userData,
}: {
  href: string;
  title: string;
  subtitle?: string | null;
  posterUrl: string | null;
  userData: UserItemData | null;
}) {
  const resume =
    !userData?.played && userData?.playedPercentage ? Math.min(userData.playedPercentage, 100) : null;

  return (
    <Link href={href} className="group flex w-full flex-col gap-1.5">
      <div className="bg-secondary relative aspect-[2/3] w-full overflow-hidden rounded-md transition-opacity group-hover:opacity-90">
        {posterUrl ? (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={posterUrl} alt={title} className="h-full w-full object-cover" />
        ) : (
          <div className="text-muted-foreground flex h-full items-center justify-center p-2 text-center font-serif text-sm">
            {title}
          </div>
        )}
        {userData?.played && (
          <span
            className="bg-brand text-brand-foreground absolute top-1.5 right-1.5 flex size-5 items-center justify-center rounded-full"
            aria-label="Watched"
          >
            <Check className="size-3.5" aria-hidden />
          </span>
        )}
        {resume != null && (
          <span className="bg-background/40 absolute inset-x-0 bottom-0 h-1" aria-label="Resume position">
            <span className="bg-brand block h-full" style={{ width: `${resume}%` }} />
          </span>
        )}
      </div>
      {/* Title lives on the poster art itself; only the type·year caption is repeated below. The title is
          still the image alt / no-poster fallback, so it stays accessible. */}
      {subtitle && <span className="text-muted-foreground truncate text-xs">{subtitle}</span>}
    </Link>
  );
}
