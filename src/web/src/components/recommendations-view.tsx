"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { cn } from "@/lib/utils";
import { toast } from "@/lib/toast";
import { errorMessage } from "@/lib/ui";
import { mediaServer, type Recommendation, type RecommendationKind } from "@/lib/media-server";
import { watchlistApi } from "@/lib/watchlist";
import { Skeleton } from "@/components/ui/skeleton";
import { ErrorState } from "@/components/states";
import { RecommendationCard } from "@/components/recommendation-card";

type KindFilter = "all" | "Movie" | "Series";
type LibraryFilter = "all" | "library" | "discover";

const KINDS: Array<{ value: KindFilter; label: string }> = [
  { value: "all", label: "All" },
  { value: "Movie", label: "Movies" },
  { value: "Series", label: "Series" },
];

const PLACES: Array<{ value: LibraryFilter; label: string }> = [
  { value: "all", label: "Everything" },
  { value: "library", label: "In library" },
  { value: "discover", label: "Not in library" },
];

/** The full recommendations page: the merged feed, its filters, and the source control. */
export function RecommendationsView() {
  const [kind, setKind] = useState<KindFilter>("all");
  const [place, setPlace] = useState<LibraryFilter>("all");

  const feed = useQuery({
    queryKey: ["recommendations", kind],
    queryFn: () => mediaServer.recommendations({ kind: kind === "all" ? undefined : kind, limit: 60 }),
  });

  const { hide, track } = useRecommendationActions();

  const items = (feed.data?.items ?? []).filter((item) =>
    place === "all" ? true : place === "library" ? item.inLibrary : !item.inLibrary,
  );

  return (
    <section className="flex flex-col gap-4">
      <header className="flex flex-col gap-1">
        <h1 className="text-2xl font-semibold tracking-tight">Recommended for you</h1>
        <p className="text-muted-foreground text-sm">
          Built from what you have watched. Titles you already finished never appear here.
        </p>
      </header>

      <div className="flex flex-wrap items-center gap-2">
        <Segmented options={KINDS} value={kind} onChange={setKind} label="Media kind" />
        <Segmented options={PLACES} value={place} onChange={setPlace} label="Availability" />
        {feed.data && feed.data.sources.length > 1 && <SourceControl feed={feed.data} />}
      </div>

      {feed.isError ? (
        <ErrorState onRetry={() => void feed.refetch()} />
      ) : feed.isPending ? (
        <Grid>
          {Array.from({ length: 12 }).map((_, index) => (
            <Skeleton key={index} className="aspect-[2/3] w-full rounded-md" />
          ))}
        </Grid>
      ) : items.length === 0 ? (
        <EmptyState hasFeed={(feed.data?.items.length ?? 0) > 0} />
      ) : (
        <Grid>
          {items.map((item) => (
            <RecommendationCard
              key={`${item.kind}:${item.tmdbId}`}
              item={item}
              onHide={hide}
              onTrack={track}
            />
          ))}
        </Grid>
      )}
    </section>
  );
}

function Grid({ children }: { children: React.ReactNode }) {
  return (
    <div className="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-6 lg:grid-cols-8">{children}</div>
  );
}

function EmptyState({ hasFeed }: { hasFeed: boolean }) {
  // Two different silences: nothing matches the filters, versus nothing to recommend at all.
  return (
    <p className="text-muted-foreground text-sm">
      {hasFeed
        ? "Nothing matches these filters."
        : "Nothing to suggest yet — finish watching something and recommendations will follow."}
    </p>
  );
}

function Segmented<T extends string>({
  options,
  value,
  onChange,
  label,
}: {
  options: Array<{ value: T; label: string }>;
  value: T;
  onChange: (value: T) => void;
  label: string;
}) {
  return (
    <div className="bg-secondary/60 flex items-center gap-0.5 rounded-md p-0.5" role="group" aria-label={label}>
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          aria-pressed={value === option.value}
          className={cn(
            "rounded px-2 py-0.5 text-xs font-medium transition-colors",
            value === option.value ? "bg-background shadow-sm" : "text-muted-foreground hover:text-foreground",
          )}
          onClick={() => onChange(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

/** Only meaningful once a second source exists, so the caller renders it conditionally. */
function SourceControl({ feed }: { feed: { sources: Array<{ key: string; displayName: string }>; selectedSources: string[] } }) {
  const queryClient = useQueryClient();
  const save = useMutation({
    mutationFn: (sources: string[] | null) => mediaServer.setRecommendationSources(sources),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["recommendations"] }),
    onError: (error) => toast.error("Couldn’t change sources", { description: errorMessage(error) }),
  });

  const toggle = (key: string) => {
    const selected = new Set(feed.selectedSources);
    if (selected.has(key)) {
      selected.delete(key);
    } else {
      selected.add(key);
    }

    // Turning the last source off would leave an unexplained empty feed; treat it as "all" instead.
    save.mutate(selected.size === 0 ? null : [...selected]);
  };

  return (
    <div className="bg-secondary/60 flex items-center gap-0.5 rounded-md p-0.5" role="group" aria-label="Sources">
      {feed.sources.map((source) => (
        <button
          key={source.key}
          type="button"
          aria-pressed={feed.selectedSources.includes(source.key)}
          disabled={save.isPending}
          className={cn(
            "rounded px-2 py-0.5 text-xs font-medium transition-colors",
            feed.selectedSources.includes(source.key)
              ? "bg-background shadow-sm"
              : "text-muted-foreground hover:text-foreground",
          )}
          onClick={() => toggle(source.key)}
        >
          {source.displayName}
        </button>
      ))}
    </div>
  );
}

/**
 * Hide (with undo) and Track, shared by the page and the home row so both behave identically.
 */
export function useRecommendationActions() {
  const queryClient = useQueryClient();
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["recommendations"] });

  const hide = (item: Recommendation) => {
    void mediaServer
      .hideRecommendation(item.kind, item.tmdbId)
      .then(() => {
        invalidate();
        toast.success(`Hid ${item.title}`, {
          // Hiding is one click and easy to misfire, so the way back must be one click too.
          action: {
            label: "Undo",
            onClick: () => {
              void mediaServer.unhideRecommendation(item.kind, item.tmdbId).then(invalidate);
            },
          },
        });
      })
      .catch((error: unknown) => toast.error("Couldn’t hide it", { description: errorMessage(error) }));
  };

  const track = (item: Recommendation) => {
    void watchlistApi
      .add({
        providerRef: { provider: "tmdb", id: item.tmdbId },
        kind: item.kind === "Series" ? "Series" : "Movie",
        title: item.title,
        year: item.year,
        posterUrl: item.posterUrl,
      })
      .then(() => {
        queryClient.invalidateQueries({ queryKey: ["watchlist"] });
        toast.success(`Tracking ${item.title}`, { description: "Its release dates will appear on the calendar." });
      })
      .catch((error: unknown) => toast.error("Couldn’t track it", { description: errorMessage(error) }));
  };

  return { hide, track };
}

export type { KindFilter, LibraryFilter, RecommendationKind };
