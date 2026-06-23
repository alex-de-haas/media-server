"use client";

import { useId, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { Search } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type MetadataCandidate, type RemapInput } from "@/lib/media-server";
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
import { ScrollArea } from "@/components/ui/scroll-area";

/**
 * Corrects a misidentified published leaf. Search a corrected title (a movie, or the owning series for an
 * episode), pick the right match, and the backend reassigns the file + rebuilds its clean library path.
 * Episodes additionally take season/episode numbers (the file moves to that slot).
 */
export function RemapDialog({
  itemId,
  mode,
  currentTitle,
  defaultSeason,
  defaultEpisode,
  open,
  onOpenChange,
  onRemapped,
}: {
  itemId: string;
  mode: "movie" | "episode";
  currentTitle: string;
  defaultSeason?: number;
  defaultEpisode?: number;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onRemapped: (targetId: string) => void;
}) {
  const isEpisode = mode === "episode";

  const titleId = useId();
  const yearId = useId();
  const [searchTitle, setSearchTitle] = useState("");
  const [searchYear, setSearchYear] = useState("");
  const [season, setSeason] = useState(defaultSeason ?? 1);
  const [episode, setEpisode] = useState(defaultEpisode ?? 1);
  const [results, setResults] = useState<MetadataCandidate[] | null>(null);

  // Reset transient state each time the dialog (re)opens so a prior search doesn't linger.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setSearchTitle("");
      setSearchYear("");
      setSeason(defaultSeason ?? 1);
      setEpisode(defaultEpisode ?? 1);
      setResults(null);
    }
  }

  const search = useMutation({
    mutationFn: () =>
      mediaServer.searchMetadata({
        title: searchTitle.trim(),
        year: searchYear.trim() ? Number(searchYear) : null,
        kind: isEpisode ? "Series" : "Movie",
      }),
    onSuccess: setResults,
    onError: (error) => toast.error("Search failed", { description: errorMessage(error) }),
  });

  const remap = useMutation({
    mutationFn: (candidate: MetadataCandidate) => {
      const input: RemapInput = {
        kind: isEpisode ? "Episode" : "Movie",
        provider: candidate.reference.provider,
        providerId: candidate.reference.id,
        title: candidate.title,
        year: candidate.year,
        season: isEpisode ? season : null,
        episode: isEpisode ? episode : null,
      };
      return mediaServer.remapLibraryItem(itemId, input);
    },
    onSuccess: ({ id }) => {
      toast.success("Match corrected");
      onRemapped(id);
    },
    onError: (error) => toast.error("Couldn’t remap item", { description: errorMessage(error) }),
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-xl">
        <DialogHeader>
          <DialogTitle>Fix match</DialogTitle>
          <DialogDescription className="truncate" title={currentTitle}>
            Reassign <span className="text-foreground font-medium">{currentTitle}</span> to the correct{" "}
            {isEpisode ? "series and episode" : "title"}.
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-3 text-sm">
          <form
            className="flex flex-wrap items-end gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              if (searchTitle.trim()) search.mutate();
            }}
          >
            <Field className="flex-1">
              <FieldLabel htmlFor={titleId}>{isEpisode ? "Series title" : "Movie title"}</FieldLabel>
              <Input
                id={titleId}
                value={searchTitle}
                placeholder={isEpisode ? "Series title" : "Movie title"}
                onChange={(e) => setSearchTitle(e.target.value)}
              />
            </Field>
            <Field className="w-20">
              <FieldLabel htmlFor={yearId}>Year</FieldLabel>
              <Input id={yearId} type="number" value={searchYear} onChange={(e) => setSearchYear(e.target.value)} />
            </Field>
            <Button type="submit" variant="secondary" size="sm" disabled={!searchTitle.trim() || search.isPending}>
              <Search />
              {search.isPending ? "Searching…" : "Search"}
            </Button>
          </form>

          {isEpisode && (
            <div className="text-muted-foreground flex items-center gap-3">
              <label className="flex items-center gap-1.5">
                Season
                <Input className="w-16" type="number" min={0} value={season} onChange={(e) => setSeason(Number(e.target.value))} />
              </label>
              <label className="flex items-center gap-1.5">
                Episode
                <Input className="w-16" type="number" min={0} value={episode} onChange={(e) => setEpisode(Number(e.target.value))} />
              </label>
            </div>
          )}

          {results === null ? (
            <span className="text-muted-foreground text-xs">Search for the correct {isEpisode ? "series" : "title"} above.</span>
          ) : results.length ? (
            <ScrollArea className="max-h-72">
              <div className="flex flex-wrap gap-2 pr-3">
                {results.map((candidate) => (
                  <Button
                    key={`${candidate.reference.provider}:${candidate.reference.id}`}
                    variant="outline"
                    size="sm"
                    disabled={remap.isPending}
                    onClick={() => remap.mutate(candidate)}
                  >
                    {candidate.title}
                    {candidate.year ? ` (${candidate.year})` : ""} · {(candidate.score * 100).toFixed(0)}%
                  </Button>
                ))}
              </div>
            </ScrollArea>
          ) : (
            <span className="text-muted-foreground text-xs">No matches for that title.</span>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}
