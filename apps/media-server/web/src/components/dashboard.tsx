"use client";

import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  mediaServer,
  type Catalog,
  type CatalogType,
  type Download,
  type IngestItem,
  type LibraryItem,
} from "@/lib/media-server";
import { ApiError } from "@/lib/api";
import { formatBytes, formatEta, formatPercent, formatSpeed } from "@/lib/format";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

const inputClass =
  "h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm outline-none focus-visible:ring-1 focus-visible:ring-ring";

const CATALOG_TYPES: CatalogType[] = ["Movie", "Series", "Anime"];

function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return error.message || `Request failed (${error.status}).`;
  }
  return error instanceof Error ? error.message : "Unexpected error.";
}

export function Dashboard() {
  return (
    <main className="mx-auto flex max-w-5xl flex-col gap-6 p-6">
      <header className="flex flex-col gap-1">
        <h1 className="text-2xl font-semibold tracking-tight">Media Server</h1>
        <p className="text-muted-foreground text-sm">
          Add a torrent, pick a catalog — the pipeline identifies, organizes, probes, enriches, and publishes it.
        </p>
      </header>

      <CatalogsSection />
      <AddTorrentSection />
      <DownloadsSection />
      <ActivitySection />
      <LibrarySection />
      <InfuseAccessSection />
    </main>
  );
}

function InfuseAccessSection() {
  const queryClient = useQueryClient();
  const credential = useQuery({ queryKey: ["jellyfin-credential"], queryFn: mediaServer.getJellyfinCredential });
  const [pin, setPin] = useState("");
  const [secret, setSecret] = useState<{ username: string; pin: string | null; serverUrl: string | null } | null>(null);

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["jellyfin-credential"] });
  const create = useMutation({
    mutationFn: () => mediaServer.createJellyfinCredential(pin),
    onSuccess: (result) => {
      setSecret(result);
      setPin("");
      invalidate();
    },
  });
  const revoke = useMutation({
    mutationFn: () => mediaServer.revokeJellyfinCredential(),
    onSuccess: () => {
      setSecret(null);
      invalidate();
    },
  });

  const status = credential.data;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Infuse access</CardTitle>
        <CardDescription>
          Create a username + PIN to sign in from a Jellyfin client (e.g. Infuse). The PIN is shown once.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3 text-sm">
        {status?.serverUrl ? (
          <p className="text-muted-foreground">
            Server URL: <span className="font-mono break-all">{status.serverUrl}</span>
          </p>
        ) : (
          <p className="text-muted-foreground">
            No public server URL configured yet — set up the Jellyfin ingress to connect external clients.
          </p>
        )}

        {status?.hasCredential ? (
          <div className="flex flex-wrap items-center gap-2">
            <span>
              Signed in as <span className="font-medium">{status.username}</span>
            </span>
            {status.locked && <Badge variant="destructive">temporarily locked</Badge>}
            {status.permanentlyLocked && <Badge variant="destructive">locked — regenerate</Badge>}
          </div>
        ) : (
          <p className="text-muted-foreground">No Infuse credential yet.</p>
        )}

        {secret && (
          <div className="rounded-md border border-dashed p-3">
            <p className="font-medium">Save this — it is shown only once.</p>
            <p>
              Username: <span className="font-mono">{secret.username}</span>
            </p>
            {secret.pin && (
              <p>
                PIN: <span className="font-mono text-base">{secret.pin}</span>
              </p>
            )}
          </div>
        )}

        <div className="flex flex-wrap items-end gap-2">
          <label className="flex flex-col gap-1">
            <span className="text-muted-foreground text-xs">PIN (optional, 6–8 digits)</span>
            <input
              className={`${inputClass} w-40`}
              inputMode="numeric"
              placeholder="auto-generate"
              value={pin}
              onChange={(e) => setPin(e.target.value.replace(/[^0-9]/g, "").slice(0, 8))}
            />
          </label>
          <Button onClick={() => create.mutate()} disabled={create.isPending}>
            {create.isPending ? "Saving…" : status?.hasCredential ? "Regenerate" : "Create credential"}
          </Button>
          {status?.hasCredential && (
            <Button variant="ghost" onClick={() => revoke.mutate()} disabled={revoke.isPending}>
              Revoke
            </Button>
          )}
        </div>
        {create.isError && <p className="text-destructive">{errorMessage(create.error)}</p>}
      </CardContent>
    </Card>
  );
}

function CatalogsSection() {
  const queryClient = useQueryClient();
  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs });
  const [name, setName] = useState("");
  const [type, setType] = useState<CatalogType>("Movie");
  const [root, setRoot] = useState("");
  const [defaultKeepSeeding, setDefaultKeepSeeding] = useState(false);

  const create = useMutation({
    mutationFn: () => mediaServer.createCatalog({ name, type, root, defaultKeepSeeding }),
    onSuccess: () => {
      setName("");
      setRoot("");
      queryClient.invalidateQueries({ queryKey: ["catalogs"] });
    },
  });

  const remove = useMutation({
    mutationFn: (id: string) => mediaServer.deleteCatalog(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["catalogs"] }),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Catalogs</CardTitle>
        <CardDescription>Destinations on one filesystem; each holds files/ and library/.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4 text-sm">
        {catalogs.data?.length ? (
          <ul className="flex flex-col gap-2">
            {catalogs.data.map((catalog) => (
              <li key={catalog.id} className="flex items-center justify-between gap-3 rounded-md border p-2">
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="font-medium">{catalog.name}</span>
                    <Badge variant="secondary">{catalog.type}</Badge>
                    {!catalog.online && <Badge variant="destructive">offline</Badge>}
                  </div>
                  <p className="text-muted-foreground truncate">{catalog.root}</p>
                </div>
                <div className="flex shrink-0 items-center gap-3">
                  <span className="text-muted-foreground">{formatBytes(catalog.freeBytes)} free</span>
                  <Button variant="ghost" size="sm" onClick={() => remove.mutate(catalog.id)}>
                    Remove
                  </Button>
                </div>
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-muted-foreground">No catalogs yet. Add one to start.</p>
        )}

        <form
          className="grid grid-cols-1 gap-2 sm:grid-cols-[1fr_8rem_2fr_auto]"
          onSubmit={(event) => {
            event.preventDefault();
            create.mutate();
          }}
        >
          <input className={inputClass} placeholder="Name" value={name} onChange={(e) => setName(e.target.value)} required />
          <select className={inputClass} value={type} onChange={(e) => setType(e.target.value as CatalogType)}>
            {CATALOG_TYPES.map((value) => (
              <option key={value} value={value}>
                {value}
              </option>
            ))}
          </select>
          <input className={inputClass} placeholder="/mnt/media/movies" value={root} onChange={(e) => setRoot(e.target.value)} required />
          <Button type="submit" disabled={create.isPending}>
            {create.isPending ? "Adding…" : "Add catalog"}
          </Button>
          <label className="text-muted-foreground flex items-center gap-2 sm:col-span-4">
            <input type="checkbox" checked={defaultKeepSeeding} onChange={(e) => setDefaultKeepSeeding(e.target.checked)} />
            Keep seeding by default
          </label>
        </form>
        {create.isError && <p className="text-destructive">{errorMessage(create.error)}</p>}
      </CardContent>
    </Card>
  );
}

function AddTorrentSection() {
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

function DownloadsSection() {
  const queryClient = useQueryClient();
  const downloads = useQuery({
    queryKey: ["downloads"],
    queryFn: mediaServer.listDownloads,
    refetchInterval: 2000,
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["downloads"] });
  const pause = useMutation({ mutationFn: mediaServer.pauseDownload, onSuccess: invalidate });
  const resume = useMutation({ mutationFn: mediaServer.resumeDownload, onSuccess: invalidate });
  const stopSeeding = useMutation({ mutationFn: mediaServer.stopSeeding, onSuccess: invalidate });
  const remove = useMutation({
    mutationFn: ({ id, deleteFiles }: { id: string; deleteFiles: boolean }) => mediaServer.removeDownload(id, deleteFiles),
    onSuccess: invalidate,
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Downloads</CardTitle>
        <CardDescription>Live torrent progress. Removing a download never deletes published library items.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-2 text-sm">
        {downloads.data?.length ? (
          downloads.data.map((download) => <DownloadRow key={download.id} download={download} onPause={() => pause.mutate(download.id)} onResume={() => resume.mutate(download.id)} onStopSeeding={() => stopSeeding.mutate(download.id)} onRemove={(deleteFiles) => remove.mutate({ id: download.id, deleteFiles })} />)
        ) : (
          <p className="text-muted-foreground">No active downloads.</p>
        )}
      </CardContent>
    </Card>
  );
}

function DownloadRow({
  download,
  onPause,
  onResume,
  onStopSeeding,
  onRemove,
}: {
  download: Download;
  onPause: () => void;
  onResume: () => void;
  onStopSeeding: () => void;
  onRemove: (deleteFiles: boolean) => void;
}) {
  const percent = download.percentComplete ?? 0;
  const eta =
    download.downloadRateBytesPerSecond && download.sizeBytes
      ? ((download.sizeBytes * (1 - percent / 100)) / download.downloadRateBytesPerSecond)
      : null;

  return (
    <div className="rounded-md border p-3">
      <div className="flex items-center justify-between gap-2">
        <span className="truncate font-medium">{download.name ?? download.infoHash}</span>
        <Badge variant="secondary">{download.engineState ?? download.state}</Badge>
      </div>
      <div className="bg-secondary mt-2 h-2 w-full overflow-hidden rounded-full">
        <div className="bg-primary h-full" style={{ width: `${Math.min(percent, 100)}%` }} />
      </div>
      <div className="text-muted-foreground mt-2 flex flex-wrap gap-x-4 gap-y-1">
        <span>{formatPercent(download.percentComplete)}</span>
        <span>↓ {formatSpeed(download.downloadRateBytesPerSecond)}</span>
        <span>↑ {formatSpeed(download.uploadRateBytesPerSecond)}</span>
        <span>ratio {download.ratio?.toFixed(2) ?? "—"}</span>
        <span>{download.peers ?? 0} peers</span>
        <span>ETA {formatEta(eta)}</span>
      </div>
      <div className="mt-2 flex flex-wrap gap-2">
        <Button variant="outline" size="sm" onClick={onResume}>Resume</Button>
        <Button variant="outline" size="sm" onClick={onPause}>Pause</Button>
        <Button variant="outline" size="sm" onClick={onStopSeeding}>Stop seeding</Button>
        <Button variant="ghost" size="sm" onClick={() => onRemove(false)}>Remove</Button>
        <Button variant="ghost" size="sm" onClick={() => onRemove(true)}>Remove + delete files</Button>
      </div>
    </div>
  );
}

function statusVariant(status: string): "default" | "secondary" | "destructive" {
  if (status === "Failed") return "destructive";
  if (status === "Done") return "default";
  return "secondary";
}

function ActivitySection() {
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
        {ingest.data?.length ? (
          ingest.data.map((item) => <IngestRow key={item.id} item={item} catalog={catalogsById.get(item.catalogId)} />)
        ) : (
          <p className="text-muted-foreground">Nothing in the pipeline.</p>
        )}
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

function LibrarySection() {
  const library = useQuery({ queryKey: ["library"], queryFn: mediaServer.listLibrary, refetchInterval: 5000 });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Library</CardTitle>
        <CardDescription>Published, playable items.</CardDescription>
      </CardHeader>
      <CardContent>
        {library.data?.length ? (
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-5">
            {library.data.map((item) => (
              <LibraryCard key={item.id} item={item} />
            ))}
          </div>
        ) : (
          <p className="text-muted-foreground text-sm">No published items yet.</p>
        )}
      </CardContent>
    </Card>
  );
}

function LibraryCard({ item }: { item: LibraryItem }) {
  return (
    <div className="flex flex-col gap-1">
      <div className="bg-secondary aspect-[2/3] w-full overflow-hidden rounded-md">
        {item.posterUrl ? (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={item.posterUrl} alt={item.title} className="h-full w-full object-cover" />
        ) : (
          <div className="text-muted-foreground flex h-full items-center justify-center p-2 text-center text-xs">{item.title}</div>
        )}
      </div>
      <span className="truncate text-sm font-medium">{item.title}</span>
      <span className="text-muted-foreground text-xs">
        {item.kind}
        {item.year ? ` · ${item.year}` : ""}
      </span>
    </div>
  );
}
