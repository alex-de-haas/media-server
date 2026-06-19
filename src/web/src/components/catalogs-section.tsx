"use client";

import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Trash2 } from "lucide-react";
import { toast } from "sonner";
import { mediaServer, type Catalog, type CatalogVolumeUsage } from "@/lib/media-server";
import { formatBytes } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { QueryState } from "@/components/states";
import { AddCatalogDialog } from "@/components/add-catalog-dialog";

// A vivid, distinguishable palette (reads on both light and dark). Each catalog gets a stable color,
// shared between its bar segment and the dot next to its name; cycles if there are more catalogs.
const SEGMENT_COLORS = [
  "#3b82f6", // blue
  "#10b981", // emerald
  "#f59e0b", // amber
  "#8b5cf6", // violet
  "#ef4444", // red
  "#14b8a6", // teal
  "#ec4899", // pink
  "#84cc16", // lime
];

export function CatalogsSection() {
  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs });
  const usage = useQuery({ queryKey: ["catalog-usage"], queryFn: mediaServer.listCatalogUsage });

  const usedById = useMemo(() => {
    const map = new Map<string, number>();
    for (const volume of usage.data ?? []) {
      for (const catalog of volume.catalogs) map.set(catalog.id, catalog.usedBytes);
    }
    return map;
  }, [usage.data]);

  // One stable color per catalog (by its position in the list), shared by the bars and the row dots.
  const colorById = useMemo(() => {
    const map = new Map<string, string>();
    (catalogs.data ?? []).forEach((catalog, index) => map.set(catalog.id, SEGMENT_COLORS[index % SEGMENT_COLORS.length]));
    return map;
  }, [catalogs.data]);

  return (
    <Card>
      <CardHeader>
        <CardTitle>Catalogs</CardTitle>
        <CardDescription>Destinations on one filesystem; each holds files/ and library/.</CardDescription>
        <CardAction>
          <AddCatalogDialog />
        </CardAction>
      </CardHeader>
      <CardContent className="flex flex-col gap-4 text-sm">
        {(usage.data?.length ?? 0) > 0 && (
          <div className="flex flex-col gap-2">
            {usage.data!.map((volume) => (
              <VolumeUsage key={volume.label} volume={volume} colors={colorById} />
            ))}
          </div>
        )}

        <QueryState query={catalogs} empty="No catalogs yet. Add one to start.">
          {(items) => (
            <ul className="flex flex-col gap-2">
              {items.map((catalog) => (
                <CatalogRow
                  key={catalog.id}
                  catalog={catalog}
                  // Once usage has loaded, a catalog absent from it (e.g. just added) is 0 used — not "free".
                  usedBytes={usage.data ? (usedById.get(catalog.id) ?? 0) : undefined}
                  color={colorById.get(catalog.id)}
                />
              ))}
            </ul>
          )}
        </QueryState>
      </CardContent>
    </Card>
  );
}

// One stacked bar per volume scaled to Σ(used) + free: each catalog's footprint, with the muted
// track = free. Non-catalog ("other") usage and total capacity are intentionally not shown — free is
// a volume-level fact shared by catalogs on the same volume.
function VolumeUsage({ volume, colors }: { volume: CatalogVolumeUsage; colors: Map<string, string> }) {
  const segments = volume.catalogs
    .filter((catalog) => catalog.usedBytes > 0)
    .map((catalog) => ({
      key: catalog.id,
      label: catalog.name,
      bytes: catalog.usedBytes,
      color: colors.get(catalog.id) ?? "var(--muted-foreground)",
    }));

  const scale = segments.reduce((sum, segment) => sum + segment.bytes, 0) + volume.freeBytes;
  const widthOf = (bytes: number) => (scale > 0 ? `${Math.min(100, (bytes / scale) * 100)}%` : "0%");

  return (
    <div className="rounded-md border p-3">
      <div className="flex items-baseline justify-between gap-2">
        <span className="truncate font-mono text-xs" title={volume.label}>
          {volume.label}
        </span>
        <span className="text-muted-foreground shrink-0 text-xs">{formatBytes(volume.freeBytes)} free</span>
      </div>

      {scale > 0 ? (
        <>
          <div className="bg-muted ring-border mt-2 flex h-2.5 w-full overflow-hidden rounded-full ring-1">
            {segments.map((segment) => (
              <Tooltip key={segment.key}>
                <TooltipTrigger
                  render={<span aria-hidden style={{ width: widthOf(segment.bytes), backgroundColor: segment.color }} className="h-full" />}
                />
                <TooltipContent>
                  {segment.label} · {formatBytes(segment.bytes)}
                </TooltipContent>
              </Tooltip>
            ))}
          </div>

          <div className="text-muted-foreground mt-2 flex flex-wrap gap-x-3 gap-y-1 text-xs">
            {segments.map((segment) => (
              <span key={segment.key} className="inline-flex items-center gap-1.5">
                <span className="size-2 shrink-0 rounded-full" style={{ backgroundColor: segment.color }} />
                {segment.label} {formatBytes(segment.bytes)}
              </span>
            ))}
            <span className="inline-flex items-center gap-1.5">
              <span className="bg-muted ring-border size-2 shrink-0 rounded-full ring-1" />
              Free {formatBytes(volume.freeBytes)}
            </span>
          </div>
        </>
      ) : (
        <p className="text-muted-foreground mt-2 text-xs">Usage unavailable — volume offline.</p>
      )}
    </div>
  );
}

function CatalogRow({
  catalog,
  usedBytes,
  color,
}: {
  catalog: Catalog;
  usedBytes: number | undefined;
  color: string | undefined;
}) {
  const queryClient = useQueryClient();
  const [confirmOpen, setConfirmOpen] = useState(false);

  const remove = useMutation({
    mutationFn: () => mediaServer.deleteCatalog(catalog.id),
    onSuccess: () => {
      setConfirmOpen(false);
      queryClient.invalidateQueries({ queryKey: ["catalogs"] });
      queryClient.invalidateQueries({ queryKey: ["catalog-usage"] });
      toast.success("Catalog removed");
    },
    onError: (error) => toast.error("Couldn’t remove catalog", { description: errorMessage(error) }),
  });

  return (
    <li className="flex items-center justify-between gap-3 rounded-md border p-2">
      <div className="min-w-0">
        <div className="flex items-center gap-2">
          <span
            className="size-2 shrink-0 rounded-full"
            style={{ backgroundColor: color ?? "var(--muted-foreground)" }}
            aria-hidden
          />
          <span className="font-medium">{catalog.name}</span>
          <Badge variant="secondary">{catalog.type}</Badge>
          {!catalog.online && <Badge variant="destructive">offline</Badge>}
        </div>
        <p className="text-muted-foreground truncate font-mono text-xs" title={catalog.root}>
          {catalog.root}
        </p>
      </div>
      <div className="flex shrink-0 items-center gap-2">
        <span className="text-muted-foreground">
          {usedBytes !== undefined ? `${formatBytes(usedBytes)} used` : `${formatBytes(catalog.freeBytes)} free`}
        </span>
        <Tooltip>
          <TooltipTrigger
            render={
              <Button variant="ghost" size="icon-sm" aria-label="Remove catalog" onClick={() => setConfirmOpen(true)}>
                <Trash2 />
              </Button>
            }
          />
          <TooltipContent>Remove</TooltipContent>
        </Tooltip>
      </div>

      <RemoveCatalogDialog
        open={confirmOpen}
        onOpenChange={(next) => {
          setConfirmOpen(next);
          if (!next) remove.reset();
        }}
        name={catalog.name}
        pending={remove.isPending}
        onConfirm={() => remove.mutate()}
      />
    </li>
  );
}

function RemoveCatalogDialog({
  open,
  onOpenChange,
  name,
  pending,
  onConfirm,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  name: string;
  pending: boolean;
  onConfirm: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Remove catalog?</DialogTitle>
          <DialogDescription>
            Remove <span className="text-foreground font-medium">{name}</span> from the catalog list. This only drops the
            catalog configuration — media files on disk are not deleted.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button type="button" variant="ghost" size="sm" disabled={pending} onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button type="button" variant="destructive" size="sm" disabled={pending} onClick={onConfirm}>
            {pending ? "Removing…" : "Remove"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
