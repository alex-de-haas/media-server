"use client";

import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, Layers } from "lucide-react";
import { mediaServer } from "@/lib/media-server";
import { PosterCard, detailHref } from "@/components/poster-card";
import { Skeleton } from "@/components/ui/skeleton";

/**
 * Collection page: a franchise header (poster, name, movie count) plus a poster grid of the member movies
 * that are in the library, reusing the same tiles the movie grids use. Reached from the Collections tab, so
 * the back control returns there.
 */
export function CollectionDetail({ id }: { id: string }) {
  const collection = useQuery({
    queryKey: ["collection", id],
    queryFn: () => mediaServer.getCollectionDetail(id),
  });

  if (collection.isError || (!collection.isPending && !collection.data)) {
    return (
      <div className="flex flex-col gap-3">
        <BackLink />
        <p className="text-muted-foreground text-sm">This collection could not be found.</p>
      </div>
    );
  }

  if (collection.isPending) {
    return <CollectionDetailSkeleton />;
  }

  const data = collection.data;

  return (
    <div className="flex flex-col gap-6">
      <BackLink />

      <header className="flex flex-col gap-4 sm:flex-row sm:gap-6">
        <div className="bg-secondary aspect-[2/3] w-28 shrink-0 self-start overflow-hidden rounded-md shadow-lg ring-1 ring-black/10 sm:w-40">
          {data.posterUrl ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img src={data.posterUrl} alt={data.name} className="h-full w-full object-cover" />
          ) : (
            <div className="text-muted-foreground flex h-full w-full items-center justify-center">
              <Layers className="size-10" aria-hidden />
            </div>
          )}
        </div>

        <div className="flex min-w-0 flex-col gap-1 sm:pt-2">
          <h1 className="font-serif text-3xl leading-tight font-medium sm:text-4xl">{data.name}</h1>
          <p className="text-muted-foreground text-sm">{data.items.length} movies</p>
        </div>
      </header>

      <div className="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-6">
        {data.items.map((item) => (
          <PosterCard
            key={item.id}
            href={detailHref(item.kind, item.id)}
            title={item.title}
            subtitle={item.year ? String(item.year) : null}
            posterUrl={item.posterUrl}
            userData={item.userData}
          />
        ))}
      </div>
    </div>
  );
}

function BackLink() {
  return (
    <Link
      href="/collections"
      className="text-muted-foreground hover:text-foreground inline-flex w-fit items-center gap-1.5 text-sm"
    >
      <ArrowLeft className="size-4" aria-hidden /> Collections
    </Link>
  );
}

function CollectionDetailSkeleton() {
  return (
    <div className="flex flex-col gap-6">
      <Skeleton className="h-5 w-24" />
      <div className="flex flex-col gap-4 sm:flex-row sm:gap-6">
        <Skeleton className="aspect-[2/3] w-28 shrink-0 rounded-md sm:w-40" />
        <div className="flex flex-1 flex-col gap-3 pt-2">
          <Skeleton className="h-9 w-2/3" />
          <Skeleton className="h-4 w-24" />
        </div>
      </div>
      <div className="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-6">
        {Array.from({ length: 6 }).map((_, index) => (
          <Skeleton key={index} className="aspect-[2/3] w-full rounded-md" />
        ))}
      </div>
    </div>
  );
}
