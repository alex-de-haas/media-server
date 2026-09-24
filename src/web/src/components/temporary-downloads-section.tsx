"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { mediaServer } from "@/lib/media-server";
import { formatBytes } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export function TemporaryDownloadsSection() {
  const client = useQueryClient();
  const [selected, setSelected] = useState<string[]>([]);
  const [preview, setPreview] = useState(false);
  const roots = useQuery({ queryKey: ["temporary-downloads"], queryFn: mediaServer.analyzeTemporaryDownloads, enabled: false });
  const cleanup = useMutation({
    mutationFn: () => mediaServer.cleanTemporaryDownloads(selected),
    onSuccess: () => { setSelected([]); setPreview(false); void roots.refetch(); },
    onSettled: () => { void client.invalidateQueries({ queryKey: ["downloads"] }); void client.invalidateQueries({ queryKey: ["ingest"] }); },
  });
  const candidates = (roots.data ?? []).filter((root) => root.id && selected.includes(root.id));
  return (
    <Card>
      <CardHeader>
        <CardTitle>Temporary download files</CardTitle>
        <CardDescription>Inspect retained downloads and retry cleanup. Downloads, active seeds and unfinished imports are protected.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <Button variant="outline" className="self-start" disabled={roots.isFetching || cleanup.isPending} onClick={() => { setPreview(false); setSelected([]); void roots.refetch(); }}>
          {roots.isFetching ? "Analyzing…" : "Analyze temporary files"}
        </Button>
        {roots.error && <p role="alert">{errorMessage(roots.error)}</p>}
        {roots.data?.length === 0 && <p className="text-muted-foreground text-sm">No temporary download directories found.</p>}
        {(roots.data ?? []).map((root) => (
          <label key={`${root.catalogId}/${root.path}`} className="flex items-start gap-2 rounded-md border p-3 text-sm">
            <input type="checkbox" disabled={!root.canClean || preview || cleanup.isPending} checked={root.id != null && selected.includes(root.id)} onChange={(event) => {
              const id = root.id!;
              setSelected((current) => event.target.checked ? [...current, id] : current.filter((value) => value !== id));
            }} />
            <span className="min-w-0 break-words">{root.catalog} · {root.path}<br />
              <span className="text-muted-foreground">{root.bytes == null ? "Size unavailable" : formatBytes(root.bytes)} · {root.state}</span>
            </span>
          </label>
        ))}
        {selected.length > 0 && !preview && <Button className="self-start" variant="outline" onClick={() => setPreview(true)}>Preview cleanup ({selected.length})</Button>}
        {preview && <div className="flex flex-col gap-2 rounded-md border p-3 text-sm">
          <p>Remove these temporary directories ({formatBytes(candidates.reduce((sum, root) => sum + (root.bytes ?? 0), 0))}):</p>
          <ul className="list-disc pl-5">{candidates.map((root) => <li key={root.id}>{root.catalog} / {root.path}</li>)}</ul>
          <p>Library files remain available. Each directory is checked again before removal.</p>
          <div className="flex gap-2">
            <Button size="sm" disabled={cleanup.isPending} onClick={() => cleanup.mutate()}>{cleanup.isPending ? "Cleaning…" : "Clean selected"}</Button>
            <Button size="sm" variant="outline" disabled={cleanup.isPending} onClick={() => setPreview(false)}>Cancel</Button>
          </div>
        </div>}
        {cleanup.error && <p role="alert">{errorMessage(cleanup.error)}</p>}
        {cleanup.data?.filter((result) => !result.cleaned).map((result) => <p role="alert" key={result.id}>{result.error}</p>)}
      </CardContent>
    </Card>
  );
}
