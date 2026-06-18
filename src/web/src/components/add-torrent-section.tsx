"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { mediaServer } from "@/lib/media-server";
import { formatBytes } from "@/lib/format";
import { inputClass, errorMessage } from "@/lib/ui";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export function AddTorrentSection() {
  const queryClient = useQueryClient();
  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs });
  const [catalogId, setCatalogId] = useState("");
  const [magnet, setMagnet] = useState("");
  const [keepSeeding, setKeepSeeding] = useState<boolean | undefined>(undefined);
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
      queryClient.invalidateQueries({ queryKey: ["downloads"] });
      queryClient.invalidateQueries({ queryKey: ["ingest"] });
    },
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
    <Card>
      <CardHeader>
        <CardTitle>Add torrent</CardTitle>
        <CardDescription>Magnet link or .torrent file, into a chosen catalog.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3 text-sm">
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
            <span className="text-muted-foreground">{formatBytes(selectedCatalog.freeBytes)} free</span>
          )}
        </div>
        <input
          className={inputClass}
          placeholder="magnet:?xt=urn:btih:…"
          value={magnet}
          onChange={(e) => setMagnet(e.target.value)}
        />
        <div className="flex flex-wrap items-center gap-3">
          <input type="file" accept=".torrent" onChange={(e) => onFile(e.target.files?.[0])} className="text-muted-foreground text-xs" />
          <label className="text-muted-foreground flex items-center gap-2">
            <input
              type="checkbox"
              checked={keepSeeding ?? false}
              onChange={(e) => setKeepSeeding(e.target.checked)}
            />
            Keep seeding
          </label>
          <Button
            onClick={() => add.mutate()}
            disabled={add.isPending || !catalogId || (!magnet.trim() && !torrentFileBase64)}
          >
            {add.isPending ? "Adding…" : "Add"}
          </Button>
        </div>
        {add.isError && <p className="text-destructive">{errorMessage(add.error)}</p>}
      </CardContent>
    </Card>
  );
}
