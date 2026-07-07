"use client";

import { useId, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { FolderInput } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type CatalogType } from "@/lib/media-server";
import { formatBytes } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Field, FieldLabel } from "@/components/ui/field";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

/** The catalog types a given item kind can move into (movies↔movie catalogs; series/anime share tvshows). */
function compatibleTypes(kind: string): CatalogType[] {
  if (kind === "Movie") return ["Movie"];
  if (kind === "Series") return ["Series", "Anime"];
  return [];
}

/**
 * Moves a published movie/series into another type-compatible catalog. Lists the eligible target catalogs
 * (right type, online, not the current one); picking one starts a background move. Because the move relocates
 * files and re-mints the item id, the caller navigates away once it is queued and the library refreshes when
 * the job completes.
 */
export function MoveToCatalogDialog({
  itemId,
  itemKind,
  itemTitle,
  currentCatalogId,
  open,
  onOpenChange,
  onMoveStarted,
}: {
  itemId: string;
  itemKind: string;
  itemTitle: string;
  currentCatalogId: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onMoveStarted: () => void;
}) {
  const selectId = useId();
  const [targetId, setTargetId] = useState("");

  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs, enabled: open });

  const allowed = compatibleTypes(itemKind);
  // Only offer online catalogs of a compatible type (excluding the current one) — an offline root would
  // just be rejected with a 409.
  const targets = (catalogs.data ?? []).filter(
    (catalog) => catalog.id !== currentCatalogId && catalog.online && allowed.includes(catalog.type),
  );
  const selected = targets.find((catalog) => catalog.id === targetId);

  // Reset the picked target each time the dialog (re)opens.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) setTargetId("");
  }

  const move = useMutation({
    mutationFn: () => mediaServer.moveLibraryItem(itemId, targetId),
    onSuccess: () => {
      toast.success("Move started", { description: `Moving “${itemTitle}” to ${selected?.name ?? "the catalog"}.` });
      onOpenChange(false);
      onMoveStarted();
    },
    onError: (error) => toast.error("Couldn’t move item", { description: errorMessage(error) }),
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Move to catalog</DialogTitle>
          <DialogDescription className="truncate" title={itemTitle}>
            Move <span className="text-foreground font-medium">{itemTitle}</span> into another catalog. Its files
            move into the target’s layout; if that catalog already has this title, they merge as extra versions.
          </DialogDescription>
        </DialogHeader>

        <Field>
          <FieldLabel htmlFor={selectId}>Target catalog</FieldLabel>
          <div className="flex flex-wrap items-center gap-3">
            <Select
              value={targetId || null}
              onValueChange={(value) => setTargetId((value as string | null) ?? "")}
              items={targets.map((catalog) => ({ value: catalog.id, label: `${catalog.name} (${catalog.type})` }))}
            >
              <SelectTrigger id={selectId} className="w-full max-w-xs">
                <SelectValue
                  placeholder={catalogs.isPending ? "Loading catalogs…" : targets.length ? "Select a catalog…" : "No compatible catalog"}
                />
              </SelectTrigger>
              <SelectContent>
                {targets.map((catalog) => (
                  <SelectItem key={catalog.id} value={catalog.id}>
                    {catalog.name} ({catalog.type})
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {selected && <span className="text-muted-foreground text-xs">{formatBytes(selected.freeBytes)} free</span>}
          </div>
          {targets.length === 0 && catalogs.isSuccess && (
            <span className="text-muted-foreground text-xs">
              Create another {itemKind === "Movie" ? "movie" : "series/anime"} catalog to move this item.
            </span>
          )}
        </Field>

        <DialogFooter>
          <Button variant="ghost" size="sm" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button size="sm" disabled={!targetId || move.isPending} onClick={() => move.mutate()}>
            <FolderInput />
            {move.isPending ? "Starting…" : "Move"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
