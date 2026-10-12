"use client";

import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTable, type ColumnDef, type ExpandedState, type SortingState } from "@tanstack/react-table";
import { AlertTriangle, ArrowDown, ArrowUp, ChevronDown, Columns3, Search } from "lucide-react";
import { useSession } from "@/components/app-shell";
import { RemovedTitleDialog } from "@/components/removed-title-dialog";
import { ManagedItem, ManagementTitleActions, SeasonDeleteControl } from "@/components/media-detail";
import { ManagementAvailability } from "@/components/management-status";
import { DataGrid, DataGridContainer, dataGridFeatures, type DataGridFeatures } from "@/components/reui/data-grid/data-grid";
import { DataGridTable } from "@/components/reui/data-grid/data-grid-table";
import { DataGridPagination } from "@/components/reui/data-grid/data-grid-pagination";
import { DataGridColumnVisibility } from "@/components/reui/data-grid/data-grid-column-visibility";
import { Filters, type FilterField, type FilterQuery } from "@/components/reui/filters/filters";
import { Badge } from "@/components/ui/badge";
import { Button, buttonVariants } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { mediaServer } from "@/lib/media-server";
import { managementApi, managementHref, managementQuery, quickViews, technicalFields, type ManagementItem } from "@/lib/management";
import { episodeLabel, formatBytes } from "@/lib/format";
import { errorMessage } from "@/lib/ui";

const optionField = (id: string, label: string, values: string[]): FilterField => ({
  id, label, type: "multiselect", operators: [{ value: "in", label: "is any of", arity: "many" }],
  options: values.map(value => ({ value, label: value })),
});
const formatFields: FilterField[] = [
  optionField("sourceKind", "Source kind", ["file", "bluray"]),
  optionField("resolution", "Resolution", ["2160p", "1080p", "720p", "480p", "unknown"]),
  optionField("codec", "Video codec", ["hevc", "h264", "av1", "vp9", "mpeg2video", "vc1"]),
  optionField("hdr", "Dynamic range", ["SDR", "HDR10", "HDR10+", "Dolby Vision", "HLG"]),
  optionField("dvProfile", "DV profile", ["5", "7", "8"]),
  optionField("state", "Source availability", ["available", "blocked", "pending", "unknown"]),
  { id: "minVersions", label: "Minimum versions", type: "number", operators: [{ value: "eq", label: "is" }] },
  { id: "maxVersions", label: "Maximum versions", type: "number", operators: [{ value: "eq", label: "is" }] },
];

export function ManagementGuard({ children }: { children: React.ReactNode }) {
  const { role } = useSession();
  return role === "admin" ? children : <div className="flex flex-col gap-3"><h1 className="text-xl font-semibold">Administrator access required</h1><Link href="/movies" className="underline">Back to library</Link></div>;
}

export function LibraryManagement({ kind, id }: { kind: "Movie" | "Series"; id?: string }) {
  const search = useSearchParams();
  const removedId = kind === "Movie" ? search.get("focus") : id;
  return <ManagementGuard>{removedId && search.get("removed") === "1"
    ? <RemovedManagement id={removedId} kind={kind} /> : <ManagementContent kind={kind} id={id} />}</ManagementGuard>;
}

function RemovedManagement({ id, kind }: { id: string; kind: "Movie" | "Series" }) {
  const router = useRouter();
  const listHref = kind === "Movie" ? "/manage/movies" : "/manage/series";
  const removed = useQuery({ queryKey: ["removed-titles"], queryFn: mediaServer.listRemovedTitles });
  const item = removed.data?.find(item => item.id === id);
  return <div className="flex flex-col gap-3"><h1 className="text-xl font-semibold">Removed title management</h1><Link className="underline" href={listHref}>Back to {kind === "Movie" ? "movie" : "series"} management</Link>
    {removed.isPending ? <p>Loading retained title…</p> : removed.isError ? <div role="alert"><p>{errorMessage(removed.error)}</p><Button variant="outline" onClick={() => removed.refetch()}>Retry</Button></div> : item ? <RemovedTitleDialog management title={item} onOpenChange={open => { if (!open) router.push(listHref); }} />
      : <p>This title is no longer in your removed list.</p>}
  </div>;
}

function ManagementContent({ kind, id }: { kind: "Movie" | "Series"; id?: string }) {
  const router = useRouter();
  const pathname = usePathname();
  const search = useSearchParams();
  const baseListHref = kind === "Movie" ? "/manage/movies" : "/manage/series";
  const returnTo = search.get("return");
  const listHref = returnTo?.startsWith(`${baseListHref}?`) ? returnTo : baseListHref;
  const currentHref = pathname + (search.size ? `?${search}` : "");
  const episodeId = kind === "Series" && id ? search.get("episode") ?? undefined : undefined;
  const tableKind = kind === "Series" && id ? "Episode" : kind;
  const focusId = kind === "Movie" ? search.get("focus") : null;
  const query = managementQuery(new URLSearchParams(search), tableKind, episodeId, tableKind === "Episode" ? id : undefined);
  const result = useQuery({ queryKey: ["management", "list", query], queryFn: () => managementApi.list(query), refetchInterval: 30_000 });
  useEffect(() => {
    if (!focusId || !result.data?.items.some(item => item.id === focusId)) return;
    // Once located, ordinary paging/Back/reload use the concrete page rather than resolving the focus again.
    const next = new URLSearchParams(search);
    next.delete("focus");
    if (result.data.offset) next.set("offset", String(result.data.offset)); else next.delete("offset");
    if (!next.has("expanded")) next.set("expanded", focusId);
    window.history.replaceState(null, "", `${pathname}?${next}`);
    document.getElementById(`management-title-${focusId}`)?.scrollIntoView({ block: "nearest" });
  }, [focusId, result.data, search, pathname]);
  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs });
  const series = useQuery({ queryKey: ["library-detail", id], queryFn: () => mediaServer.getLibraryDetail(id!), enabled: kind === "Series" && !!id, retry: false });
  const [searchDraft, setSearchDraft] = useState<string | null>(null);
  const update = (patch: Record<string, string | null>, resetPage = true) => {
    const next = new URLSearchParams(window.location.search);
    next.delete("focus");
    if (resetPage) { next.delete("offset"); next.delete("expanded"); }
    for (const [key, value] of Object.entries(patch)) { if (value) next.set(key, value); else next.delete(key); }
    window.history.pushState(null, "", `${pathname}${next.size ? `?${next}` : ""}`);
  };
  const fields: FilterField[] = [
    { id: "catalogId", label: "Catalog", type: "select", operators: [{ value: "eq", label: "is" }],
      options: (catalogs.data ?? []).filter(c => kind === "Movie" ? c.type === "Movie" : ["Series", "Anime"].includes(c.type)).map(c => ({ value: c.id, label: c.name })) },
    ...formatFields,
  ];
  const filterQuery: FilterQuery = { id: "management", type: "group", combinator: "and", rules: technicalFields.flatMap(key => {
    const value = search.get(key);
    return value ? [{ id: key, type: "rule" as const, path: [key], operator: ["catalogId", "minVersions", "maxVersions"].includes(key) ? "eq" : "in",
      value: key.endsWith("Versions") ? Number(value) : key === "catalogId" ? value : value.split(",") }] : [];
  }) };
  const onFilters = (value: FilterQuery) => {
    if (value.rules.some(rule => rule.type === "rule" && (!rule.operator || rule.value === undefined))) return;
    const patch: Record<string, string | null> = Object.fromEntries(technicalFields.map(key => [key, null]));
    for (const rule of value.rules) {
      if (rule.type !== "rule" || rule.negated || !technicalFields.includes(rule.path[0] as typeof technicalFields[number])) continue;
      if (rule.value !== undefined && rule.value !== "") patch[rule.path[0]] = Array.isArray(rule.value) ? rule.value.join(",") : String(rule.value);
    }
    update(patch);
  };
  const expansion: ExpandedState = Object.fromEntries((search.get("expanded")?.split(",") ?? (focusId ? [focusId] : episodeId ? [episodeId] : [])).map(key => [key, true]));
  const pagination = { pageIndex: Math.floor(Number(search.get("offset") || 0) / Number(search.get("limit") || 25)), pageSize: Number(search.get("limit") || 25) };
  const sorting: SortingState = [{ id: search.get("sort") || (tableKind === "Episode" ? "episode" : "title"), desc: search.get("descending") === "true" }];
  const hidden = search.get("hidden")?.split(",") ?? ["year"];
  const backHref = tableKind === "Episode" ? `/manage/series/${id}` : currentHref;
  const seriesParams = new URLSearchParams([...search.entries()].filter(([key]) => !["offset", "expanded", "sort", "descending"].includes(key)));
  const affectedParams = new URLSearchParams(seriesParams);
  if (!affectedParams.has("view") || affectedParams.get("view") === "all") affectedParams.set("view", "attention");
  const clear = () => { setSearchDraft(null); window.history.pushState(null, "", pathname); };
  const columns: ColumnDef<DataGridFeatures, ManagementItem>[] = [
    { id: "expand", header: "", size: 42, enableHiding: false, enableSorting: false,
      cell: ({ row }) => tableKind === "Series" ? null : <Button variant="ghost" size="icon-sm" aria-label={`${row.getIsExpanded() ? "Hide" : "Show"} versions of ${row.original.title}`} aria-expanded={row.getIsExpanded()} onClick={() => row.toggleExpanded()}><ChevronDown className={row.getIsExpanded() ? "" : "-rotate-90"} /></Button>,
      meta: { expandedContent: (row: ManagementItem) => <div className="max-w-full p-3"><ManagedItem showActions={false} id={row.id} query={query} sourceId={search.get("source") ?? undefined} seriesId={tableKind === "Episode" ? id : undefined} backHref={backHref} /></div> } },
    { id: "title", accessorKey: "title", size: 240, minSize: 180, enableHiding: false,
      header: ({ column }) => <SortHeader label="Title" column={column} />,
      cell: ({ row }) => <div className="flex min-w-0 flex-col gap-1">
        {tableKind === "Series" ? <Link className="font-medium underline-offset-4 hover:underline" href={`/manage/series/${row.original.id}?${seriesParams}`}>{row.original.title}</Link> : <span id={`management-title-${row.original.id}`} className="font-medium">{row.original.title}</span>}
        <span className="text-muted-foreground text-xs">{tableKind === "Episode" ? episodeLabel(row.original.parentIndexNumber, row.original.indexNumber, row.original.indexNumberEnd) : row.original.year ?? "Year unknown"}</span>
      </div> },
    { id: "year", accessorKey: "year", header: ({ column }) => <SortHeader label="Year" column={column} />, size: 80 },
    { id: "catalog", accessorKey: "catalogName", header: "Catalog", size: 130, enableSorting: false },
    { id: "versions", accessorKey: "versionCount", header: ({ column }) => <SortHeader label="Versions" column={column} />, size: 100,
      cell: ({ row }) => tableKind === "Series" ? <span>{row.original.seasonCount} seasons · {row.original.episodeCount} episodes · {row.original.versionCount} versions</span> : row.original.versionCount },
    { id: "size", accessorKey: "sizeBytes", header: ({ column }) => <SortHeader label="Size" column={column} />, size: 110, cell: ({ row }) => row.original.sizeBytes == null ? "Unknown" : formatBytes(row.original.sizeBytes) },
    { id: "availability", accessorKey: "availability", header: "Availability", size: 120, enableSorting: false,
      cell: ({ row }) => <ManagementAvailability value={row.original.availability} /> },
    { id: "issues", accessorKey: "issueCount", header: ({ column }) => <SortHeader label={tableKind === "Series" ? "Affected episodes" : "Source issues"} column={column} />, size: 145,
      cell: ({ row }) => tableKind === "Series" ? <Link className="underline underline-offset-4" href={`/manage/series/${row.original.id}?${affectedParams}`}>{row.original.affectedEpisodes > 0 ? <Badge variant="warning"><AlertTriangle aria-hidden />{row.original.affectedEpisodes}</Badge> : 0}</Link>
        : row.original.issueCount > 0 ? <Badge variant="warning"><AlertTriangle aria-hidden />{row.original.issueCount}</Badge> : <span className="text-muted-foreground">—</span> },
    { id: "operations", accessorKey: "activeOperations", header: "Operations", size: 110, enableSorting: false,
      cell: ({ row }) => row.original.activeOperations ? <Link href="/activity" className="underline">{row.original.activeOperations} active</Link> : "—" },
    { id: "actions", header: "Actions", size: 100, enableHiding: false, enableSorting: false,
      cell: ({ row }) => <ManagementTitleActions id={row.original.id} seriesId={tableKind === "Episode" ? id : undefined} backHref={backHref}
        libraryHref={tableKind === "Episode" ? `/series/${id}` : `/${kind === "Movie" ? "movies" : "series"}/${row.original.id}`} /> },
  ];
  const table = useTable({
    features: dataGridFeatures, data: result.data?.items ?? [], columns, getRowId: row => row.id,
    getRowCanExpand: () => tableKind !== "Series", manualPagination: true, manualSorting: true, autoResetExpanded: false, autoResetPageIndex: false,
    rowCount: result.data?.total ?? 0, state: { expanded: expansion, pagination, sorting, columnVisibility: Object.fromEntries(hidden.map(key => [key, false])) },
    onExpandedChange: updater => {
      const next = typeof updater === "function" ? updater(expansion) : updater;
      update({ expanded: next === true ? (result.data?.items ?? []).map(row => row.id).join(",") : Object.keys(next).filter(key => next[key]).join(",") || "none" }, false);
    },
    onPaginationChange: updater => { const next = typeof updater === "function" ? updater(pagination) : updater; update({ offset: String(next.pageIndex * next.pageSize), limit: String(next.pageSize) }, false); },
    onSortingChange: updater => { const next = typeof updater === "function" ? updater(sorting) : updater; update({ sort: next[0]?.id || "title", descending: String(next[0]?.desc ?? false) }); },
    onColumnVisibilityChange: updater => { const old = Object.fromEntries(hidden.map(key => [key, false])); const next = typeof updater === "function" ? updater(old) : updater; update({ hidden: Object.keys(next).filter(key => !next[key]).join(",") || "none" }, false); },
  });
  return <div className="flex min-w-0 flex-col gap-5">
    <div className="flex flex-wrap items-start justify-between gap-3"><div><h1 className="text-2xl font-semibold tracking-tight">Library management</h1><p className="text-muted-foreground mt-1 text-sm">Versions, source diagnostics and media operations</p></div><Link className={buttonVariants({ variant: "outline" })} href={kind === "Movie" ? "/movies" : "/series"}>Open library</Link></div>
    <Tabs value={kind} onValueChange={value => router.push(value === "Movie" ? "/manage/movies" : "/manage/series")}><TabsList><TabsTrigger value="Movie">Movies</TabsTrigger><TabsTrigger value="Series">Series</TabsTrigger></TabsList></Tabs>
    {returnTo && listHref !== baseListHref && <Link href={listHref} className="text-primary text-sm underline">Back to previous list</Link>}
    {focusId && result.isSuccess && !result.data.items.some(item => item.id === focusId) && <div role="status" className="flex flex-wrap items-center gap-3 text-sm">
      <span>This movie is no longer in the published list or does not match these filters.</span>
      <Link className="underline" href={managementHref("Movie", focusId, undefined, { removed: "1" })}>View retained title</Link>
      <Button variant="ghost" size="sm" onClick={() => update({ focus: null }, false)}>Dismiss</Button>
    </div>}
    {id && <div className="flex flex-col gap-3"><Link href={listHref} className="text-primary text-sm underline">All {kind === "Movie" ? "movies" : "series"}</Link>
      {kind === "Series" && series.data && <div className="flex flex-wrap items-center gap-3"><h2 className="text-xl font-semibold">{series.data.title}</h2><ManagementTitleActions id={id} backHref={listHref} libraryHref={`/series/${id}`} /></div>}
      {kind === "Series" && series.isError && <p role="alert">This series is unavailable or has been merged. <Button variant="link" onClick={() => series.refetch()}>Retry</Button></p>}
      {kind === "Series" && series.data && <div className="flex flex-wrap gap-2" aria-label="Seasons"><Button size="sm" variant={!search.has("season") ? "secondary" : "outline"} onClick={() => update({ season: null, episode: null })}>All seasons</Button>{(series.data.seasons ?? []).map(season => <div key={season.id} className="flex items-center gap-1"><Button size="sm" variant={search.get("season") === String(season.seasonNumber ?? 0) ? "secondary" : "outline"} onClick={() => update({ season: String(season.seasonNumber ?? 0), episode: null })}>Season {season.seasonNumber ?? 0}</Button><SeasonDeleteControl season={season.seasonNumber ?? 0} seasonId={season.id} episodeCount={season.episodeCount} seriesId={id} backHref={listHref} /></div>)}</div>}
      {episodeId && <Button variant="link" className="self-start" onClick={() => update({ episode: null })}>Show all matching episodes</Button>}
    </div>}
    <div className="flex flex-wrap gap-2" aria-label="Quick views">{quickViews.map(([value, label]) => <Button key={value} size="sm" variant={(search.get("view") || "all") === value ? "secondary" : "outline"} aria-pressed={(search.get("view") || "all") === value} onClick={() => update({ view: value })}>{label}<span className="text-muted-foreground tabular-nums">{result.data?.counts[value] ?? "—"}</span></Button>)}</div>
    <form className="flex flex-wrap items-center gap-2" onSubmit={event => { event.preventDefault(); update({ search: searchDraft ?? search.get("search") }); setSearchDraft(null); }}><Input className="max-w-sm" aria-label="Search titles" placeholder="Search titles…" value={searchDraft ?? search.get("search") ?? ""} onChange={event => setSearchDraft(event.target.value)} /><Button variant="outline" type="submit"><Search />Search</Button><Button variant="ghost" onClick={clear}>Reset</Button></form>
    <ManagementFilters fields={fields} query={filterQuery} onChange={onFilters} />
    <p className="text-muted-foreground text-xs">Assessment: {result.data?.reference ?? "Apple/tvOS · HDR10 and Dolby Vision · stored media facts"}. Device support can vary. Unknown preparation is not a playback failure.</p>
    {result.isError ? <div role="alert" className="flex flex-col items-start gap-2"><p>{errorMessage(result.error)}</p><Button variant="outline" onClick={() => result.refetch()}>Retry</Button></div> : <DataGrid table={table} recordCount={result.data?.total ?? 0} isLoading={result.isPending} loadingMode="skeleton" emptyMessage="No titles match these filters." tableLayout={{ width: "fixed", rowBorder: true, headerBackground: true }}>
      <div className="flex justify-end"><DataGridColumnVisibility table={table} trigger={<Button variant="outline" size="sm"><Columns3 />Columns</Button>} /></div>
      <DataGridContainer className="rounded-lg border"><div className="overflow-x-auto"><DataGridTable /></div></DataGridContainer>
      <DataGridPagination sizes={[25, 50, 100]} />
    </DataGrid>}
  </div>;
}
// Keep the registry's draft and rule identities while a selection updates the URL.
// Back/Forward replaces values; an internally committed selection keeps its open picker.
function ManagementFilters({ fields, query, onChange }: { fields: FilterField[]; query: FilterQuery; onChange: (query: FilterQuery) => void }) {
  const signature = (value: FilterQuery) => JSON.stringify(value.rules.filter(rule => rule.type === "rule")
    .map(rule => [rule.path[0], rule.operator, rule.value]).sort((a, b) => String(a[0]).localeCompare(String(b[0]))));
  const urlKey = signature(query);
  const [draft, setDraft] = useState({ urlKey, query });
  if (draft.urlKey !== urlKey) setDraft({ urlKey, query: signature(draft.query) === urlKey ? draft.query : query });
  return <Filters fields={fields} query={draft.query} onQueryChange={next => { setDraft({ urlKey, query: next }); onChange(next); }}
    onBeforeQueryChange={value => value.combinator === "and" && value.rules.every(rule => rule.type === "rule" && !rule.negated)
      && new Set(value.rules.filter(rule => rule.type === "rule").map(rule => rule.path[0])).size === value.rules.length}
    variant="basic" showClear size="sm" />;
}

function SortHeader({ label, column }: { label: string; column: { getIsSorted: () => false | "asc" | "desc"; toggleSorting: () => void } }) {
  const direction = column.getIsSorted();
  return <Button variant="ghost" size="sm" onClick={() => column.toggleSorting()}>{label}{direction === "asc" ? <ArrowUp /> : direction === "desc" ? <ArrowDown /> : null}</Button>;
}
