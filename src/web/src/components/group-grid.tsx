"use client";

import Link from "next/link";
import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, Folder } from "lucide-react";
import { count } from "@/lib/catalog-scan";
import { groupsApi, groupTypeLabel } from "@/lib/groups";
import type { LibraryItem } from "@/lib/media-server";
import { PosterCard, detailHref } from "@/components/poster-card";
import { QueryState, ErrorState, Loading } from "@/components/states";
import { Button } from "@/components/ui/button";

export function GroupGrid() {
  const groups = useQuery({ queryKey: ["groups"], queryFn: groupsApi.list, refetchInterval: 5000 });
  return <QueryState query={groups} empty="No groups yet. An administrator can create them in Settings → Groups.">
    {(items) => <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
      {items.map((group) => <Link key={group.id} href={`/groups/${group.id}`}
        className="bg-card hover:bg-accent focus-visible:ring-ring flex items-center gap-4 rounded-xl border p-5 transition-colors focus-visible:ring-2">
        <Folder className="text-muted-foreground size-12 shrink-0" aria-hidden />
        <div className="min-w-0">
          <h2 className="truncate font-semibold">{group.name}</h2>
          <p className="text-muted-foreground text-sm">{groupTypeLabel(group.catalogType)} · {count(group.itemCount, "title")} · {group.kind === "smart" ? "Smart" : "Manual"}</p>
        </div>
      </Link>)}
    </div>}
  </QueryState>;
}

export function GroupPosters({ items }: { items: LibraryItem[] }) {
  return <div className="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-6">
    {items.map((item) => <PosterCard key={item.id} href={detailHref(item.kind, item.id)} title={item.title}
      subtitle={item.year ? String(item.year) : null} posterUrl={item.posterUrl} userData={item.userData} />)}
  </div>;
}

export function GroupDetail({ id }: { id: string }) {
  const [offset, setOffset] = useState(0);
  const query = useQuery({ queryKey: ["groups", id, offset], queryFn: () => groupsApi.detail(id, offset), refetchInterval: 5000 });
  return <div className="flex flex-col gap-6">
    <Link href="/groups" className="text-muted-foreground inline-flex w-fit items-center gap-2 text-sm"><ArrowLeft className="size-4" /> Groups</Link>
    {query.isPending ? <Loading /> : query.isError ? <ErrorState onRetry={() => void query.refetch()} /> : <>
      <header><h1 className="text-3xl font-semibold">{query.data.name}</h1>
        <p className="text-muted-foreground mt-2 text-sm">{groupTypeLabel(query.data.catalogType)} · {count(query.data.total, "title")}</p></header>
      {query.data.items.length ? <GroupPosters items={query.data.items} /> : <p className="text-muted-foreground">No matching titles on this page.</p>}
      <div className="flex items-center gap-3">
        <Button variant="outline" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - 60))}>Previous</Button>
        <span className="text-muted-foreground text-sm">Page {Math.floor(offset / 60) + 1}</span>
        <Button variant="outline" disabled={offset + query.data.items.length >= query.data.total} onClick={() => setOffset(offset + 60)}>Next</Button>
      </div>
    </>}
  </div>;
}
