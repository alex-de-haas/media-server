"use client";

import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { mediaServer, type Catalog, type IngestItem } from "@/lib/media-server";
import { inputClass } from "@/lib/ui";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { QueryState } from "@/components/states";

function statusVariant(status: string): "default" | "secondary" | "destructive" {
  if (status === "Failed") return "destructive";
  if (status === "Done") return "default";
  return "secondary";
}

export function ActivitySection() {
  const ingest = useQuery({ queryKey: ["ingest"], queryFn: mediaServer.listIngest, refetchInterval: 3000 });
  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs });
  const catalogsById = useMemo(
    () => new Map((catalogs.data ?? []).map((catalog) => [catalog.id, catalog])),
    [catalogs.data],
  );

  return (
    <Card>
      <CardHeader>
        <CardTitle>Pipeline activity</CardTitle>
        <CardDescription>Each item flows intake → identify → download → organize → probe → enrich → publish.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-2 text-sm">
        <QueryState query={ingest} empty="Nothing in the pipeline.">
          {(items) => items.map((item) => <IngestRow key={item.id} item={item} catalog={catalogsById.get(item.catalogId)} />)}
        </QueryState>
      </CardContent>
    </Card>
  );
}

function IngestRow({ item, catalog }: { item: IngestItem; catalog: Catalog | undefined }) {
  const queryClient = useQueryClient();
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["ingest"] });
  const retry = useMutation({ mutationFn: () => mediaServer.retryIngest(item.id), onSuccess: invalidate });

  return (
    <div className="rounded-md border p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <Badge variant={statusVariant(item.status)}>{item.status}</Badge>
          <span className="text-muted-foreground">stage: {item.stage}</span>
          {catalog && <span className="text-muted-foreground">· {catalog.name}</span>}
        </div>
        {(item.status === "Failed" || item.status === "NeedsReview") && (
          <Button variant="outline" size="sm" onClick={() => retry.mutate()}>Retry</Button>
        )}
      </div>
      {item.lastError && <p className="text-destructive mt-1">{item.lastError}</p>}
      {item.status === "NeedsReview" && (
        <ReviewPanel item={item} catalog={catalog} onMatched={invalidate} />
      )}
    </div>
  );
}

function ReviewPanel({ item, catalog, onMatched }: { item: IngestItem; catalog: Catalog | undefined; onMatched: () => void }) {
  const isEpisodic = catalog?.type === "Series" || catalog?.type === "Anime";
  const unresolved = item.sourceFiles.filter((file) => file.assignmentStatus === "NeedsReview" || file.mediaItemId == null);
  const [season, setSeason] = useState(1);
  const [episode, setEpisode] = useState(1);

  const match = useMutation({
    mutationFn: ({ sourceFileId, provider, providerId, title, year }: { sourceFileId: string; provider: string; providerId: string; title: string; year: number | null }) =>
      mediaServer.matchIngest(item.id, {
        sourceFileId,
        kind: isEpisodic ? "Episode" : "Movie",
        provider,
        providerId,
        title,
        year,
        season: isEpisodic ? season : null,
        episode: isEpisodic ? episode : null,
      }),
    onSuccess: onMatched,
  });

  return (
    <div className="mt-2 flex flex-col gap-2 border-t pt-2">
      {isEpisodic && (
        <div className="text-muted-foreground flex items-center gap-2">
          <label className="flex items-center gap-1">S<input className={`${inputClass} h-7 w-14`} type="number" min={0} value={season} onChange={(e) => setSeason(Number(e.target.value))} /></label>
          <label className="flex items-center gap-1">E<input className={`${inputClass} h-7 w-14`} type="number" min={0} value={episode} onChange={(e) => setEpisode(Number(e.target.value))} /></label>
        </div>
      )}
      {unresolved.map((file) => (
        <div key={file.id} className="flex flex-col gap-1">
          <span className="text-muted-foreground truncate">{file.relativePath}</span>
          <div className="flex flex-wrap gap-2">
            {item.reviewCandidates.length ? (
              item.reviewCandidates.map((candidate) => (
                <Button
                  key={candidate.reference.id}
                  variant="outline"
                  size="sm"
                  onClick={() =>
                    match.mutate({
                      sourceFileId: file.id,
                      provider: candidate.reference.provider,
                      providerId: candidate.reference.id,
                      title: candidate.title,
                      year: candidate.year,
                    })
                  }
                >
                  {candidate.title}
                  {candidate.year ? ` (${candidate.year})` : ""} · {(candidate.score * 100).toFixed(0)}%
                </Button>
              ))
            ) : (
              <span className="text-muted-foreground">No candidates returned.</span>
            )}
          </div>
        </div>
      ))}
    </div>
  );
}
