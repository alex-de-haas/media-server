"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { toast } from "sonner";
import { mediaServer } from "@/lib/media-server";
import { formatBytes } from "@/lib/format";
import { inputClass, errorMessage } from "@/lib/ui";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

export function AddTorrentDialog() {
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs });
  const [catalogId, setCatalogId] = useState("");
  const [magnet, setMagnet] = useState("");
  const [keepSeeding, setKeepSeeding] = useState(false);
  const [torrentFileBase64, setTorrentFileBase64] = useState<string | undefined>(undefined);

  const selectedCatalog = catalogs.data?.find((catalog) => catalog.id === catalogId);

  const add = useMutation({
    mutationFn: () =>
      mediaServer.addTorrent({
        catalogId,
        magnet: magnet.trim() || undefined,
        torrentFileBase64,
        keepSeeding,
      }),
    onSuccess: () => {
      setMagnet("");
      setTorrentFileBase64(undefined);
      setOpen(false);
      queryClient.invalidateQueries({ queryKey: ["downloads"] });
      queryClient.invalidateQueries({ queryKey: ["ingest"] });
      toast.success("Torrent added");
    },
    onError: (error) => toast.error("Couldn’t add torrent", { description: errorMessage(error) }),
  });

  async function onFile(file: File | undefined) {
    if (!file) {
      setTorrentFileBase64(undefined);
      return;
    }
    const buffer = await file.arrayBuffer();
    let binary = "";
    const bytes = new Uint8Array(buffer);
    for (let i = 0; i < bytes.length; i += 1) {
      binary += String.fromCharCode(bytes[i]);
    }
    setTorrentFileBase64(btoa(binary));
  }

  return (
    <>
      <Button size="sm" onClick={() => setOpen(true)}>
        <Plus />
        Add torrent
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add torrent</DialogTitle>
            <DialogDescription>Magnet link or .torrent file, into a chosen catalog.</DialogDescription>
          </DialogHeader>

          <form
            className="flex flex-col gap-3 text-sm"
            onSubmit={(e) => {
              e.preventDefault();
              if (catalogId && (magnet.trim() || torrentFileBase64)) add.mutate();
            }}
          >
            <label className="flex flex-col gap-1">
              <span className="text-muted-foreground text-xs">Catalog</span>
              <div className="flex flex-wrap items-center gap-3">
                <select className={`${inputClass} max-w-xs`} value={catalogId} onChange={(e) => setCatalogId(e.target.value)}>
                  <option value="">Select a catalog…</option>
                  {catalogs.data?.map((catalog) => (
                    <option key={catalog.id} value={catalog.id}>
                      {catalog.name} ({catalog.type})
                    </option>
                  ))}
                </select>
                {selectedCatalog && (
                  <span className="text-muted-foreground text-xs">{formatBytes(selectedCatalog.freeBytes)} free</span>
                )}
              </div>
            </label>

            <label className="flex flex-col gap-1">
              <span className="text-muted-foreground text-xs">Magnet link</span>
              <input
                className={inputClass}
                placeholder="magnet:?xt=urn:btih:…"
                value={magnet}
                onChange={(e) => setMagnet(e.target.value)}
              />
            </label>

            <label className="flex flex-col gap-1">
              <span className="text-muted-foreground text-xs">…or a .torrent file</span>
              <input
                type="file"
                accept=".torrent"
                onChange={(e) => onFile(e.target.files?.[0])}
                className="text-muted-foreground file:bg-secondary file:text-secondary-foreground file:mr-3 file:rounded-md file:border-0 file:px-2.5 file:py-1 file:text-xs text-xs"
              />
            </label>

            <label className="flex items-center gap-2">
              <Checkbox checked={keepSeeding} onCheckedChange={(checked) => setKeepSeeding(checked === true)} />
              <span>Keep seeding after download completes</span>
            </label>

            <DialogFooter className="mt-2">
              <Button type="button" variant="ghost" size="sm" onClick={() => setOpen(false)}>
                Cancel
              </Button>
              <Button type="submit" size="sm" disabled={add.isPending || !catalogId || (!magnet.trim() && !torrentFileBase64)}>
                {add.isPending ? "Adding…" : "Add torrent"}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </>
  );
}
