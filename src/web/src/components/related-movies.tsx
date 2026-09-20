"use client";

import { useQuery } from "@tanstack/react-query";
import { mediaServer } from "@/lib/media-server";
import { PosterCard, detailHref } from "@/components/poster-card";
import { Rail, RailItem } from "@/components/rail";

export function RelatedMovies({ id, backHref }: { id: string; backHref: string }) {
  const searchIndex = backHref.indexOf("?");
  const browseSearch = searchIndex < 0 ? "" : backHref.slice(searchIndex);
  return <><RelatedRow id={id} section="collection" browseSearch={browseSearch} /><RelatedRow id={id} section="similar" browseSearch={browseSearch} /></>;
}

function RelatedRow({ id, section, browseSearch }: { id: string; section: "collection" | "similar"; browseSearch: string }) {
  const query = useQuery({ queryKey: ["related-movies", id, section], queryFn: () => mediaServer.relatedMovies(id, section) });
  if (!query.data?.items.length) return null;
  return (
    <Rail title={section === "collection" ? `More from ${query.data.collectionName ?? "this collection"}` : "Similar movies in your library"}>
      {query.data.items.map((item) => (
        <RailItem key={item.id}>
          <PosterCard href={`${detailHref("Movie", item.id)}${browseSearch}`} title={item.title} subtitle={item.year?.toString()}
            posterUrl={item.posterUrl} userData={item.userData} />
        </RailItem>
      ))}
    </Rail>
  );
}
