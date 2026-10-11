"use client";
import { useQueries } from "@tanstack/react-query";
import { AlertTriangle, Clock } from "lucide-react";
import { managementApi, type LibraryAvailability } from "@/lib/management";
import { Badge } from "@/components/ui/badge";

/** Bounded batches, shared by poster and episode lists; never one request per card. */
export function useLibraryAvailability(ids: string[]) {
  const stable = [...new Set(ids)].sort();
  const batches = Array.from({ length: Math.ceil(stable.length / 100) }, (_, index) => stable.slice(index * 100, (index + 1) * 100));
  const queries = useQueries({ queries: batches.map(batch => ({
    queryKey: ["library-availability", batch], queryFn: () => managementApi.availability(batch), staleTime: 30_000, refetchInterval: 30_000,
  })) });
  return new Map(queries.flatMap(query => query.data ?? []).map(value => [value.id, value]));
}
export function availabilityBadge(value?: LibraryAvailability) {
  return value?.availability === "blocked" ? "Unavailable" : value?.availability === "pending" ? "Preparing" : undefined;
}
export function AvailabilityNotice({ value }: { value?: LibraryAvailability }) {
  const label = availabilityBadge(value);
  if (!label && !value?.dolbyVisionFallback) return null;
  return <div className="flex flex-wrap items-center gap-2 text-xs">
    {label && <Badge variant="outline">{value?.availability === "pending" ? <Clock aria-hidden /> : <AlertTriangle aria-hidden />}{label} for Apple/tvOS</Badge>}
    {value?.dolbyVisionFallback && <span className="text-muted-foreground">Dolby Vision plays as HDR10 on Apple/tvOS.</span>}
  </div>;
}
