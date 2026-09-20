"use client";

import { useQuery } from "@tanstack/react-query";
import { mediaServer } from "@/lib/media-server";
import { PosterCard, detailHref } from "@/components/poster-card";
import { Rail, RailItem } from "@/components/rail";

export function RelatedMovies({ id }: { id: string }) {
  return <><RelatedRow id={id} section="collection" /><RelatedRow id={id} section="similar" /></>;
}

function RelatedRow({ id, section }: { id: string; section: "collection" | "similar" }) {
  const query = useQuery({ queryKey: ["related-movies", id, section], queryFn: () => mediaServer.relatedMovies(id, section) });
  if (!query.data?.items.length) return null;
  return (
    <Rail title={section === "collection" ? `More from ${query.data.collectionName ?? "this collection"}` : "Similar movies in your library"}>
      {query.data.items.map((item) => (
        <RailItem key={item.id}>
          <PosterCard href={detailHref("Movie", item.id)} title={item.title} subtitle={item.year?.toString()}
            posterUrl={item.posterUrl} userData={item.userData} />
        </RailItem>
      ))}
    </Rail>
  );
}
