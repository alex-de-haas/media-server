"use client";

import { useEffect, useTransition } from "react";
import { usePathname, useRouter } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { mediaServer } from "@/lib/media-server";
import { catalogAppliesToKind, withCatalog, type LibraryKind } from "@/lib/catalog-navigation";
import { PosterCard, detailHref } from "@/components/poster-card";
import { QueryState } from "@/components/states";
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";

const ALL_CATALOGS = "__all_catalogs__";

/**
 * Catalog-aware poster grid for one top-level media kind. The backend applies both filters; the catalog
 * remains in the URL so refresh, history, detail navigation, and realtime cache invalidation stay coherent.
 */
export function LibraryGrid({ title, kind, catalogId }: { title: string; kind: LibraryKind; catalogId?: string }) {
  const pathname = usePathname();
  const router = useRouter();
  const [navigationPending, startNavigation] = useTransition();
  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs });
  const applicableCatalogs = (catalogs.data ?? []).filter((catalog) => catalogAppliesToKind(catalog.type, kind));
  const selectedCatalog = applicableCatalogs.find((catalog) => catalog.id === catalogId);
  const catalogIsValid = !catalogId || selectedCatalog !== undefined;

  // Old bookmarks can reference a deleted catalog or one of the wrong media type. Once the catalog list
  // proves that context invalid, normalize the route to the unfiltered page instead of leaving an empty trap.
  useEffect(() => {
    if (catalogId && catalogs.isSuccess && !catalogIsValid) {
      router.replace(pathname, { scroll: false });
    }
  }, [catalogId, catalogIsValid, catalogs.isSuccess, pathname, router]);

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
    startNavigation(() => router.push(withCatalog(pathname, nextCatalogId), { scroll: false }));
  };

  return (
    <>
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
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
