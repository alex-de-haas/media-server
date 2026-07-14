"use client";

import { useId, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { CalendarPlus, Search } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type MetadataCandidate } from "@/lib/media-server";
import { watchlistApi, type TrackedKind } from "@/lib/watchlist";
import { errorMessage } from "@/lib/ui";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Field, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

/**
 * "Add title" on the calendar: search TMDb for a movie or series — in the library or not — and put it on
 * the calendar. Reuses the metadata-search flow of the review/remap dialogs, with poster rows so the right
 * candidate is recognizable at a glance.
 */
export function AddTitleDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const titleId = useId();
  const kindId = useId();
  const yearId = useId();
  const queryClient = useQueryClient();

  const [kind, setKind] = useState<TrackedKind>("Movie");
  const [searchTitle, setSearchTitle] = useState("");
  const [searchYear, setSearchYear] = useState("");
  const [results, setResults] = useState<MetadataCandidate[] | null>(null);

  // Reset transient state each time the dialog (re)opens so a prior search doesn't linger.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setKind("Movie");
      setSearchTitle("");
      setSearchYear("");
      setResults(null);
    }
  }

  const search = useMutation({
    mutationFn: () =>
      mediaServer.searchMetadata({
        title: searchTitle.trim(),
        year: searchYear.trim() ? Number(searchYear) : null,
        kind,
      }),
    onSuccess: setResults,
    onError: (error) => toast.error("Search failed", { description: errorMessage(error) }),
  });

  const track = useMutation({
    mutationFn: (candidate: MetadataCandidate) =>
      watchlistApi.add({
        providerRef: { provider: candidate.reference.provider, id: candidate.reference.id },
        kind,
        title: candidate.title,
        year: candidate.year,
        posterUrl: candidate.posterUrl ?? null,
      }),
    onSuccess: (item) => {
      toast.success(`Tracking “${item.title}”`, { description: "Its release dates are being fetched now." });
      void queryClient.invalidateQueries({ queryKey: ["watchlist"] });
      void queryClient.invalidateQueries({ queryKey: ["watchlist-calendar"] });
      onOpenChange(false);
    },
    onError: (error) => toast.error("Couldn’t track the title", { description: errorMessage(error) }),
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[85vh] max-w-xl">
        <DialogHeader>
          <DialogTitle>Add a title to your calendar</DialogTitle>
          <DialogDescription>Search TMDb — the title doesn’t have to be in the library.</DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-3 text-sm">
          <form
            className="flex flex-wrap items-end gap-2"
            onSubmit={(event) => {
              event.preventDefault();
              if (searchTitle.trim()) search.mutate();
            }}
          >
            <Field className="w-28">
              <FieldLabel htmlFor={kindId}>Kind</FieldLabel>
              <Select
                value={kind}
                onValueChange={(value) => {
                  setKind(value as TrackedKind);
                  setResults(null);
                }}
              >
                <SelectTrigger id={kindId} className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="Movie">Movie</SelectItem>
                  <SelectItem value="Series">Series</SelectItem>
                </SelectContent>
              </Select>
            </Field>
            <Field className="flex-1">
              <FieldLabel htmlFor={titleId}>Title</FieldLabel>
              <Input
                id={titleId}
                value={searchTitle}
                placeholder={kind === "Movie" ? "Movie title" : "Series title"}
                onChange={(event) => setSearchTitle(event.target.value)}
              />
            </Field>
            <Field className="w-20">
              <FieldLabel htmlFor={yearId}>Year</FieldLabel>
              <Input id={yearId} type="number" value={searchYear} onChange={(event) => setSearchYear(event.target.value)} />
            </Field>
            <Button type="submit" variant="secondary" size="sm" disabled={!searchTitle.trim() || search.isPending}>
              <Search />
              {search.isPending ? "Searching…" : "Search"}
            </Button>
          </form>

          {results === null ? (
            <span className="text-muted-foreground text-xs">Search for a {kind === "Movie" ? "movie" : "series"} above.</span>
          ) : results.length ? (
            <div className="-mr-2 flex max-h-80 flex-col gap-1 overflow-y-auto pr-2">
              {results.map((candidate) => (
                <div
                  key={`${candidate.reference.provider}:${candidate.reference.id}`}
                  className="hover:bg-secondary/60 flex items-center gap-3 rounded-md p-1.5"
                >
                  <div className="bg-secondary h-14 w-10 shrink-0 overflow-hidden rounded">
                    {candidate.posterUrl && (
                      // eslint-disable-next-line @next/next/no-img-element
                      <img src={candidate.posterUrl} alt="" className="h-full w-full object-cover" />
                    )}
                  </div>
                  <div className="min-w-0 flex-1">
                    <p className="truncate font-medium">{candidate.title}</p>
                    <p className="text-muted-foreground text-xs">{candidate.year ?? "Year unknown"}</p>
                  </div>
                  <Button variant="outline" size="sm" disabled={track.isPending} onClick={() => track.mutate(candidate)}>
                    <CalendarPlus className="size-4" aria-hidden /> Track
                  </Button>
                </div>
              ))}
            </div>
          ) : (
            <span className="text-muted-foreground text-xs">No matches for that title.</span>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}
