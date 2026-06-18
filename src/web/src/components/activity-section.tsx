"use client";

import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  mediaServer,
  type Catalog,
  type IngestItem,
  type IngestSourceFile,
  type MetadataCandidate,
} from "@/lib/media-server";
import { formatTimeAgo } from "@/lib/format";
import { inputClass } from "@/lib/ui";
import { useSession } from "@/components/app-shell";
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

// A human explanation of why an item sits where it does — especially the "Pending with no error"
// states that otherwise look identical and inert. Mirrors the pipeline stage semantics on the server.
function stateHint(item: IngestItem): string | null {
  switch (item.status) {
    case "Running":
      return "Processing…";
    case "Pending":
      if (item.lastError) return null; // shown as an error + retry countdown below.
      if (item.stage === "Download") return "Waiting for the download to finish.";
      if (item.sourceFiles.length === 0) return "Waiting for the torrent's file list — no files received yet.";
      return "Queued, waiting to be processed.";
    default:
      return null;
  }
}

function IngestRow({ item, catalog }: { item: IngestItem; catalog: Catalog | undefined }) {
  const { role } = useSession();
  const queryClient = useQueryClient();
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["ingest"] });
  const retry = useMutation({ mutationFn: () => mediaServer.retryIngest(item.id), onSuccess: invalidate });
  const remove = useMutation({ mutationFn: () => mediaServer.deleteIngest(item.id), onSuccess: invalidate });

  const title = item.downloadName ?? item.sourceFiles[0]?.relativePath ?? "Untitled item";
  const age = formatTimeAgo(item.createdAt);
  const hint = stateHint(item);

  return (
    <div className="rounded-md border p-3">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="flex min-w-0 flex-col gap-1">
          <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
            <Badge variant={statusVariant(item.status)}>{item.status}</Badge>
            <span className="text-muted-foreground">stage: {item.stage}</span>
            {catalog && <span className="text-muted-foreground">· {catalog.name}</span>}
            {age && <span className="text-muted-foreground">· added {age}</span>}
            {item.attemptCount > 0 && <span className="text-muted-foreground">· attempt {item.attemptCount}/5</span>}
          </div>
          <p className="truncate font-medium" title={title}>{title}</p>
          {hint && <p className="text-muted-foreground text-xs">{hint}</p>}
          {item.status !== "NeedsReview" && item.sourceFiles.length > 0 && <FileSummary files={item.sourceFiles} />}
        </div>
        <div className="flex shrink-0 gap-2">
          {(item.status === "Failed" || item.status === "NeedsReview") && (
            <Button variant="outline" size="sm" onClick={() => retry.mutate()} disabled={retry.isPending}>Retry</Button>
          )}
          {role === "admin" && (
            <Button
              variant="ghost"
              size="sm"
              className="text-destructive"
              onClick={() => remove.mutate()}
              disabled={remove.isPending}
            >
              Remove
            </Button>
          )}
        </div>
      </div>
      {item.lastError && <p className="text-destructive mt-1 text-sm">{item.lastError}</p>}
      {item.status === "NeedsReview" && (
        <ReviewPanel item={item} catalog={catalog} onMatched={invalidate} />
      )}
    </div>
  );
}

function FileSummary({ files }: { files: IngestSourceFile[] }) {
  const shown = files.slice(0, 2);
  const rest = files.length - shown.length;
  return (
    <ul className="text-muted-foreground flex flex-col gap-0.5 font-mono text-xs">
      {shown.map((file) => (
        <li key={file.id} className="truncate" title={file.relativePath}>
          {file.relativePath}
        </li>
      ))}
      {rest > 0 && <li>+{rest} more file{rest > 1 ? "s" : ""}</li>}
    </ul>
  );
}

function ReviewPanel({ item, catalog, onMatched }: { item: IngestItem; catalog: Catalog | undefined; onMatched: () => void }) {
  const isEpisodic = catalog?.type === "Series" || catalog?.type === "Anime";
  const unresolved = item.sourceFiles.filter((file) => file.assignmentStatus === "NeedsReview" || file.mediaItemId == null);
  const [season, setSeason] = useState(1);
  const [episode, setEpisode] = useState(1);
  const [searchTitle, setSearchTitle] = useState("");
  const [searchYear, setSearchYear] = useState("");
  // Results from a manual re-search override the (possibly empty/wrong) auto-identified candidates.
  const [searchResults, setSearchResults] = useState<MetadataCandidate[] | null>(null);

  const search = useMutation({
    mutationFn: () =>
      mediaServer.searchIngest(item.id, {
        title: searchTitle.trim(),
        year: searchYear.trim() ? Number(searchYear) : null,
        kind: isEpisodic ? "Series" : "Movie",
      }),
    onSuccess: (results) => setSearchResults(results),
  });

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

  const candidates = searchResults ?? item.reviewCandidates;

  return (
    <div className="mt-2 flex flex-col gap-2 border-t pt-2">
      {/* Re-search by a corrected title when the auto-parsed name was wrong or returned nothing. */}
      <form
        className="flex flex-wrap items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          if (searchTitle.trim()) search.mutate();
        }}
      >
        <label className="flex flex-col gap-1">
          <span className="text-muted-foreground text-xs">Corrected title</span>
          <input
            className={`${inputClass} h-8 w-56`}
            value={searchTitle}
            placeholder={isEpisodic ? "Series title" : "Movie title"}
            onChange={(e) => setSearchTitle(e.target.value)}
          />
        </label>
        <label className="flex flex-col gap-1">
          <span className="text-muted-foreground text-xs">Year</span>
          <input className={`${inputClass} h-8 w-20`} type="number" value={searchYear} onChange={(e) => setSearchYear(e.target.value)} />
        </label>
        <Button type="submit" variant="secondary" size="sm" disabled={!searchTitle.trim() || search.isPending}>
          {search.isPending ? "Searching…" : "Search"}
        </Button>
      </form>
      {search.isError && <p className="text-destructive text-xs">Search failed. Try again.</p>}

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
            {candidates.length ? (
              candidates.map((candidate) => (
                <Button
                  key={`${candidate.reference.provider}:${candidate.reference.id}`}
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
              <span className="text-muted-foreground">{searchResults ? "No matches for that title." : "No candidates returned — try a corrected title above."}</span>
            )}
          </div>
        </div>
      ))}
    </div>
  );
}
