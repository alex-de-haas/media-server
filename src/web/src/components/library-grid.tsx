"use client";

import { useQuery } from "@tanstack/react-query";
import { mediaServer } from "@/lib/media-server";
import { PosterCard, detailHref } from "@/components/poster-card";
import { EmptyState, QueryState } from "@/components/states";

/**
 * Poster grid of published items. The internal `/api/library` returns top-level movies and series;
 * `kind` filters that list client-side for the Movies / Series tabs (Home passes no filter). Cards
 * link to the item's detail page.
 */
export function LibraryGrid({ kind }: { kind?: "Movie" | "Series" }) {
  const library = useQuery({ queryKey: ["library"], queryFn: mediaServer.listLibrary, refetchInterval: 5000 });

  return (
    <QueryState query={library} empty="No published items yet.">
      {(all) => {
        const items = all.filter((item) => !kind || item.kind === kind);
        if (!items.length) {
          return <EmptyState>Nothing here yet.</EmptyState>;
        }
        return (
          <div className="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-6">
            {items.map((item) => (
              <PosterCard
                key={item.id}
                href={detailHref(item.kind, item.id)}
                title={item.title}
                subtitle={`${item.kind}${item.year ? ` · ${item.year}` : ""}`}
                posterUrl={item.posterUrl}
                userData={item.userData}
              />
            ))}
          </div>
        );
      }}
    </QueryState>
  );
}
