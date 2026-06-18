"use client";

import type { ReactNode } from "react";
import type { UseQueryResult } from "@tanstack/react-query";

export function Loading({ label = "Loading…" }: { label?: string }) {
  return <p className="text-muted-foreground text-sm">{label}</p>;
}

export function EmptyState({ children }: { children: ReactNode }) {
  return <p className="text-muted-foreground text-sm">{children}</p>;
}

export function ErrorState({ onRetry }: { onRetry?: () => void }) {
  return (
    <p className="text-destructive flex flex-wrap items-center gap-2 text-sm">
      Couldn&rsquo;t load this.
      {onRetry && (
        <button type="button" onClick={onRetry} className="text-foreground underline underline-offset-2">
          Retry
        </button>
      )}
    </p>
  );
}

/** Renders the loading / error / empty states for a list query, or the children with the data. */
export function QueryState<T>({
  query,
  empty,
  children,
}: {
  query: UseQueryResult<T[]>;
  empty: ReactNode;
  children: (data: T[]) => ReactNode;
}) {
  if (query.isPending) {
    return <Loading />;
  }
  if (query.isError) {
    return <ErrorState onRetry={() => void query.refetch()} />;
  }
  if (!query.data || query.data.length === 0) {
    return <EmptyState>{empty}</EmptyState>;
  }
  return <>{children(query.data)}</>;
}
