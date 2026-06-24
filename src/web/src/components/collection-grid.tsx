"use client";

import { useQuery } from "@tanstack/react-query";
import { Layers } from "lucide-react";
import { mediaServer } from "@/lib/media-server";
import { PosterCard } from "@/components/poster-card";
import { EmptyState, QueryState } from "@/components/states";
import { Skeleton } from "@/components/ui/skeleton";

/**
 * Poster grid of movie franchises. The internal `/api/library/collections` returns each collection with at
 * least two owned movies; cards link to the collection's member list. Mirrors {@link LibraryGrid}.
 */
export function CollectionGrid() {
  const collections = useQuery({
    queryKey: ["collections"],
    queryFn: mediaServer.listCollections,
    refetchInterval: 5000,
  });

  return (
    <QueryState query={collections} empty="No collections yet." pending={<PosterGridSkeleton />}>
      {(items) => {
        if (!items.length) {
          return <EmptyState icon={Layers}>Nothing here yet.</EmptyState>;
        }
        return (
          <div className="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-6">
            {items.map((collection) => (
              <PosterCard
                key={collection.id}
                href={`/collections/${collection.id}`}
                title={collection.name}
                subtitle={`${collection.itemCount} movies`}
                posterUrl={collection.posterUrl}
                userData={null}
              />
            ))}
          </div>
        );
      }}
    </QueryState>
  );
}

function PosterGridSkeleton() {
  return (
    <div className="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-6">
      {Array.from({ length: 12 }).map((_, index) => (
        <div key={index} className="flex flex-col gap-1.5">
          <Skeleton className="aspect-[2/3] w-full rounded-md" />
          <Skeleton className="h-3.5 w-3/4" />
          <Skeleton className="h-3 w-1/2" />
        </div>
      ))}
    </div>
  );
}
