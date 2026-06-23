"use client";

import type { ReactNode } from "react";
import type { UseQueryResult } from "@tanstack/react-query";
import { AlertTriangle, type LucideIcon } from "lucide-react";
import { Alert, AlertAction, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty";

export function Loading({ label = "Loading…" }: { label?: string }) {
  return <p className="text-muted-foreground text-sm">{label}</p>;
}

export function EmptyState({
  children,
  icon: Icon,
  title,
}: {
  children: ReactNode;
  icon?: LucideIcon;
  title?: string;
}) {
  return (
    <Empty className="py-10">
      <EmptyHeader>
        {Icon && (
          <EmptyMedia variant="icon">
            <Icon />
          </EmptyMedia>
        )}
        {title && <EmptyTitle>{title}</EmptyTitle>}
        <EmptyDescription>{children}</EmptyDescription>
      </EmptyHeader>
    </Empty>
  );
}

export function ErrorState({ onRetry }: { onRetry?: () => void }) {
  return (
    <Alert variant="destructive">
      <AlertTriangle />
      <AlertTitle>Couldn&rsquo;t load this.</AlertTitle>
      {onRetry && (
        <AlertAction>
          <Button type="button" variant="outline" size="sm" onClick={onRetry}>
            Retry
          </Button>
        </AlertAction>
      )}
    </Alert>
  );
}

/** Renders the loading / error / empty states for a list query, or the children with the data. */
export function QueryState<T>({
  query,
  empty,
  pending,
  children,
}: {
  query: UseQueryResult<T[]>;
  empty: ReactNode;
  /** Optional placeholder shown while the query is pending (e.g. a skeleton). Defaults to a text line. */
  pending?: ReactNode;
  children: (data: T[]) => ReactNode;
}) {
  if (query.isPending) {
    return <>{pending ?? <Loading />}</>;
  }
  if (query.isError) {
    return <ErrorState onRetry={() => void query.refetch()} />;
  }
  if (!query.data || query.data.length === 0) {
    return <EmptyState>{empty}</EmptyState>;
  }
  return <>{children(query.data)}</>;
}
