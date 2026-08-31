"use client";

import { useEffect, useState, useTransition } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { mediaServer, type RemovedTitle } from "@/lib/media-server";
import {
  catalogAppliesToKind,
  removedSearchParam,
  withCatalog,
  withRemoved,
  type LibraryKind,
} from "@/lib/catalog-navigation";
import { PosterCard, detailHref } from "@/components/poster-card";
import { RemovedTitleDialog } from "@/components/removed-title-dialog";
import { QueryState } from "@/components/states";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";

const ALL_CATALOGS = "__all_catalogs__";

/**
 * Catalog-aware poster grid for one top-level media kind. The backend applies both filters; the catalog
 * remains in the URL so refresh, history, detail navigation, and realtime cache invalidation stay coherent.
 *
 * It can also show the user's **removed** titles — the ones whose files are gone but whose history,
 * rating or favorite kept the record alive. They are off by default and never mixed into the library
 * itself: they sit after it, dimmed and badged, because they are not things to watch. The toggle only
 * appears when this user has some of this kind; for most libraries it is never there at all.
 */
export function LibraryGrid({ title, kind, catalogId }: { title: string; kind: LibraryKind; catalogId?: string }) {
  const pathname = usePathname();
  const router = useRouter();
  const searchParams = useSearchParams();
  const search = searchParams.toString();
  const currentHref = search ? `${pathname}?${search}` : pathname;
  const showRemoved = removedSearchParam(searchParams.get("removed") ?? undefined);
  const [navigationPending, startNavigation] = useTransition();
  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs });
  const applicableCatalogs = (catalogs.data ?? []).filter((catalog) => catalogAppliesToKind(catalog.type, kind));
  const selectedCatalog = applicableCatalogs.find((catalog) => catalog.id === catalogId);
  const catalogIsValid = !catalogId || selectedCatalog !== undefined;

  // Old bookmarks can reference a deleted catalog or one of the wrong media type. Once the catalog list
  // proves that context invalid, normalize the route to the unfiltered page instead of leaving an empty trap.
  useEffect(() => {
    if (catalogId && catalogs.isSuccess && !catalogIsValid) {
      router.replace(withCatalog(currentHref, undefined), { scroll: false });
    }
  }, [catalogId, catalogIsValid, catalogs.isSuccess, currentHref, router]);

  // While catalogs are loading, keep honoring the URL and let the API validate the id. Once resolved, an
  // invalid id falls back to All catalogs at the same time as the URL normalization above.
  const effectiveCatalogId = !catalogs.isSuccess || catalogIsValid ? catalogId : undefined;
  const library = useQuery({
    queryKey: ["library", kind, effectiveCatalogId ?? "all"],
    queryFn: () => mediaServer.listLibrary({ kind, catalogId: effectiveCatalogId }),
    refetchInterval: 5000,
  });
  const itemLabel = kind === "Series" ? "series" : "movies";
  const emptyMessage = selectedCatalog
    ? `No ${itemLabel} in ${selectedCatalog.name} yet.`
    : "No published items yet.";

  const selectItems = [
    { value: ALL_CATALOGS, label: "All catalogs" },
    ...applicableCatalogs.map((catalog) => ({
      value: catalog.id,
      label: `${catalog.name}${catalog.online ? "" : " (Offline)"}`,
    })),
  ];

  const changeCatalog = (value: string | null) => {
    const nextCatalogId = value && value !== ALL_CATALOGS ? value : undefined;
    startNavigation(() => router.push(withCatalog(currentHref, nextCatalogId), { scroll: false }));
  };

  // Removed titles are the signed-in user's own, so they carry no catalog: a ghost outlives the catalog
  // it was deleted from, and one deleted with its catalog has none at all.
  const removed = useQuery({ queryKey: ["removed-titles"], queryFn: mediaServer.listRemovedTitles });
  const ghosts = (removed.data ?? []).filter((ghost) => ghost.kind === kind);
  const [openGhost, setOpenGhost] = useState<RemovedTitle | null>(null);

  return (
    <>
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
        <div className="flex flex-wrap items-center gap-4">
          {ghosts.length > 0 && (
            <div className="flex items-center gap-2">
              <Checkbox
                id="show-removed"
                checked={showRemoved}
                onCheckedChange={(checked: boolean | "indeterminate") =>
                  startNavigation(() => router.push(withRemoved(currentHref, checked === true), { scroll: false }))
                }
                disabled={navigationPending}
              />
              <Label htmlFor="show-removed" className="text-muted-foreground text-xs font-medium">
                Show removed ({ghosts.length})
              </Label>
            </div>
          )}
          {catalogs.isSuccess && applicableCatalogs.length > 1 && (
            <div className="flex items-center gap-2">
              <span className="text-muted-foreground text-xs font-medium">Catalog</span>
              <Select
                value={selectedCatalog?.id ?? ALL_CATALOGS}
                onValueChange={(value) => changeCatalog(value as string | null)}
                items={selectItems}
                disabled={navigationPending}
              >
                <SelectTrigger size="sm" className="min-w-44" aria-label={`Filter ${itemLabel} by catalog`}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent align="end">
                  <SelectGroup>
                    <SelectItem value={ALL_CATALOGS}>All catalogs</SelectItem>
                    {applicableCatalogs.map((catalog) => (
                      <SelectItem key={catalog.id} value={catalog.id}>
                        {catalog.name}{catalog.online ? "" : " (Offline)"}
                      </SelectItem>
                    ))}
                  </SelectGroup>
                </SelectContent>
              </Select>
            </div>
          )}
        </div>
      </div>

      <QueryState query={library} empty={emptyMessage} pending={<PosterGridSkeleton />}>
        {(items) => (
          <div className="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-6">
            {items.map((item) => (
              <PosterCard
                key={item.id}
                href={detailHref(item.kind, item.id, effectiveCatalogId)}
                title={item.title}
                subtitle={`${item.kind}${item.year ? ` · ${item.year}` : ""}`}
                posterUrl={item.posterUrl}
                userData={item.userData}
              />
            ))}
          </div>
        )}
      </QueryState>

      {showRemoved && ghosts.length > 0 && (
        <section className="flex flex-col gap-3">
          <div>
            <h2 className="text-lg font-semibold tracking-tight">Removed</h2>
            <p className="text-muted-foreground text-xs">
              No longer in the library. Your watch history, ratings and favorites are kept, and a re-added
              title picks them back up automatically.
            </p>
          </div>
          <div className="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-6">
            {ghosts.map((ghost) => (
              <PosterCard
                key={ghost.id}
                onSelect={() => setOpenGhost(ghost)}
                title={ghost.title}
                subtitle={ghost.year ? `${ghost.kind} · ${ghost.year}` : ghost.kind}
                posterUrl={ghost.posterUrl}
                userData={null}
                badge="Removed"
                dimmed
              />
            ))}
          </div>
        </section>
      )}

      <RemovedTitleDialog title={openGhost} onOpenChange={(open) => !open && setOpenGhost(null)} />
    </>
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
