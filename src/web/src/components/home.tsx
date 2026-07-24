"use client";

import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, Download, FolderTree, type LucideIcon } from "lucide-react";
import { mediaServer, type LibraryRailItem } from "@/lib/media-server";
import { cn } from "@/lib/utils";
import { useSession } from "@/components/app-shell";
import { Rail, RailItem } from "@/components/rail";
import { RecommendationCard } from "@/components/recommendation-card";
import { useRecommendationActions } from "@/components/recommendations-view";
import { PosterCard, detailHref } from "@/components/poster-card";
import { Skeleton } from "@/components/ui/skeleton";

export function Home() {
  const session = useSession();
  const resume = useQuery({ queryKey: ["resume"], queryFn: mediaServer.listResume });
  const nextUp = useQuery({ queryKey: ["nextup"], queryFn: mediaServer.listNextUp });
  const recent = useQuery({ queryKey: ["recent"], queryFn: mediaServer.listRecent });

  return (
    <div className="flex flex-col gap-8">
      <header className="flex flex-col gap-1">
        <h1 className="text-2xl font-semibold tracking-tight">Media Server</h1>
        <p className="text-muted-foreground text-sm">Pick up where you left off, or browse what&rsquo;s new.</p>
      </header>

      {session.role === "admin" && <OpsStrip />}

      {resume.data?.length ? <RailRow title="Continue watching" items={resume.data} /> : null}
      {nextUp.data?.length ? <RailRow title="Next up" items={nextUp.data} /> : null}
      <RecommendedRow />

      {recent.isPending ? (
        <RailSkeleton title="Recently added" />
      ) : recent.data?.length ? (
        <Rail title="Recently added">
          {recent.data.map((item) => (
            <RailItem key={item.id}>
              <PosterCard
                href={detailHref(item.kind, item.id)}
                title={item.title}
                subtitle={`${item.kind}${item.year ? ` · ${item.year}` : ""}`}
                posterUrl={item.posterUrl}
                userData={item.userData}
              />
            </RailItem>
          ))}
        </Rail>
      ) : (
        <p className="text-muted-foreground text-sm">Nothing published yet — add a torrent from the Downloads tab.</p>
      )}
    </div>
  );
}

/**
 * The recommendations rail. Rendered only when there is something to say — a user who has watched
 * nothing yet should see the page they already know, not an empty promise.
 */
function RecommendedRow() {
  const feed = useQuery({
    queryKey: ["recommendations", "home"],
    queryFn: () => mediaServer.recommendations({ limit: 12 }),
  });
  const { hide, track } = useRecommendationActions();

  if (!feed.data?.items.length) {
    return null;
  }

  return (
    <section className="flex flex-col gap-3">
      <div className="flex items-baseline justify-between gap-2">
        <h2 className="text-lg font-semibold tracking-tight">Recommended for you</h2>
        <Link href="/recommendations" className="text-muted-foreground hover:text-foreground text-sm">
          See all
        </Link>
      </div>
      <div className="flex gap-3 overflow-x-auto pb-1">
        {feed.data.items.map((item) => (
          <div key={`${item.kind}:${item.tmdbId}`} className="w-28 shrink-0 sm:w-32">
            <RecommendationCard item={item} onHide={hide} onTrack={track} />
          </div>
        ))}
      </div>
    </section>
  );
}

function RailRow({ title, items }: { title: string; items: LibraryRailItem[] }) {
  return (
    <Rail title={title}>
      {items.map((item) => (
        <RailItem key={item.id}>
          <PosterCard
            href={`/${item.navKind === "Series" ? "series" : "movies"}/${item.navId}`}
            title={item.title}
            subtitle={item.subtitle}
            posterUrl={item.posterUrl}
            userData={item.userData}
          />
        </RailItem>
      ))}
    </Rail>
  );
}

function RailSkeleton({ title }: { title: string }) {
  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold tracking-tight">{title}</h2>
      <div className="flex gap-3 overflow-hidden pb-1">
        {Array.from({ length: 6 }).map((_, index) => (
          <div key={index} className="w-28 shrink-0 sm:w-32">
            <Skeleton className="aspect-[2/3] w-full rounded-md" />
          </div>
        ))}
      </div>
    </section>
  );
}

function OpsStrip() {
  // SSE (RealtimeBridge) keeps these fresh; the slow interval is only a reconnect fallback.
  const downloads = useQuery({ queryKey: ["downloads"], queryFn: mediaServer.listDownloads, refetchInterval: 20000 });
  const ingest = useQuery({ queryKey: ["ingest"], queryFn: mediaServer.listIngest, refetchInterval: 20000 });
  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs });

  const active = downloads.data?.filter((download) => (download.percentComplete ?? 0) < 100).length ?? 0;
  const review = ingest.data?.filter((item) => item.status === "NeedsReview").length ?? 0;
  const offline = catalogs.data?.filter((catalog) => !catalog.online).length ?? 0;

  return (
    <div className="grid grid-cols-3 gap-3">
      <OpStat href="/activity" icon={Download} label="Downloading" value={active} />
      <OpStat href="/activity" icon={AlertTriangle} label="Needs review" value={review} warn={review > 0} />
      <OpStat href="/catalogs" icon={FolderTree} label="Catalogs offline" value={offline} warn={offline > 0} />
    </div>
  );
}

function OpStat({
  href,
  icon: Icon,
  label,
  value,
  warn,
}: {
  href: string;
  icon: LucideIcon;
  label: string;
  value: number;
  warn?: boolean;
}) {
  return (
    <Link href={href} className="hover:bg-muted/50 flex flex-col gap-1 rounded-md border p-3 transition-colors">
      <span className="text-muted-foreground inline-flex items-center gap-1.5 text-xs">
        <Icon className="size-3.5" aria-hidden /> {label}
      </span>
      <span className={cn("text-2xl font-semibold", warn && "text-destructive")}>{value}</span>
    </Link>
  );
}
