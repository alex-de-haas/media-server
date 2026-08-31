"use client";

import Link from "next/link";
import { Check } from "lucide-react";
import type { UserItemData } from "@/lib/media-server";
import { withCatalog } from "@/lib/catalog-navigation";

/** Detail-page route for a top-level item. Movies and series are separate tabs. */
export function detailHref(kind: string, id: string, catalogId?: string): string {
  return withCatalog(kind === "Series" ? `/series/${id}` : `/movies/${id}`, catalogId);
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

// A poster tile used in both the library grids and the Home rails. The title sits under the art with
// the type·year caption beneath it; the amber accent carries the resume bar and the watched badge.
//
// It leads somewhere or it does something: `href` makes it a link to a detail page, `onSelect` a button
// that opens something in place. A removed title takes the second form — it has no page to go to, only
// the user's own marks left to manage.
//
// Exactly one of the two, spelled as a union so a card that does nothing at all cannot be written: the
// tile is the whole hit target, and an inert one looks identical to a working one.
type PosterCardProps = {
  title: string;
  subtitle?: string | null;
  posterUrl: string | null;
  userData: UserItemData | null;
  badge?: string;
  dimmed?: boolean;
} & ({ href: string; onSelect?: never } | { href?: never; onSelect: () => void });

export function PosterCard({
  href,
  onSelect,
  title,
  subtitle,
  posterUrl,
  userData,
  badge,
  dimmed = false,
}: PosterCardProps) {
  const resume =
    !userData?.played && userData?.playedPercentage ? Math.min(userData.playedPercentage, 100) : null;

  const body = (
    <>
      <div
        className={`bg-secondary relative aspect-[2/3] w-full overflow-hidden rounded-md transition-opacity group-hover:opacity-90${
          dimmed ? " opacity-60" : ""
        }`}
      >
        {posterUrl ? (
          // Decorative: the title below the art already names the link, and an alt repeating it would make
          // a screen reader announce the same name twice.
          // eslint-disable-next-line @next/next/no-img-element
          <img src={posterUrl} alt="" className="h-full w-full object-cover" />
        ) : (
          // The title is rendered below the card, so the empty art says only that art is what is missing —
          // and says it to the eye alone: inside the link, the words would join the link's accessible name.
          <div
            aria-hidden
            className="text-muted-foreground flex h-full items-center justify-center p-2 text-center text-xs"
          >
            No poster
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
        {badge && (
          <span className="bg-background/85 text-foreground absolute top-1.5 left-1.5 rounded px-1.5 py-0.5 text-[10px] font-medium">
            {badge}
          </span>
        )}
      </div>
      {/* Poster art alone identifies a title only for someone who recognises it, so the name is spelled out
          under the art, with the type·year caption below it. `title` carries the full text for a name the
          single line has to truncate. */}
      <div className="flex min-w-0 flex-col gap-0.5">
        <span className="truncate text-[13px] font-medium" title={title}>
          {title}
        </span>
        {subtitle && <span className="text-muted-foreground truncate text-xs">{subtitle}</span>}
      </div>
    </>
  );

  const className = "group flex w-full flex-col gap-1.5";
  return href ? (
    <Link href={href} className={className}>
      {body}
    </Link>
  ) : (
    <button type="button" onClick={onSelect} className={`${className} text-left`}>
      {body}
    </button>
  );
}
