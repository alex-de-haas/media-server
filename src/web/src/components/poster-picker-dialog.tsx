"use client";

import { useQuery, useQueryClient, useMutation } from "@tanstack/react-query";
import { Check, RotateCcw } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer } from "@/lib/media-server";
import { errorMessage } from "@/lib/ui";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { cn } from "@/lib/utils";

/**
 * Which poster this title shows.
 *
 * The server ranks artwork by language — a poster should carry a readable title — but TMDb does not always
 * have one: for a sequel the localized poster is often the textless international art, and then no ranking
 * can tell which film the picture is of. This is where the operator answers instead. Every candidate is
 * already cached, so opening this costs no provider request.
 */
export function PosterPickerDialog({
  itemId,
  title,
  open,
  onOpenChange,
}: {
  itemId: string;
  title: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const queryClient = useQueryClient();
  const images = useQuery({
    queryKey: ["item-images", itemId],
    queryFn: () => mediaServer.listItemImages(itemId),
    // Only while the dialog is open: the candidate list is not part of the page behind it.
    enabled: open,
  });

  const pin = useMutation({
    mutationFn: (tag: string | null) => mediaServer.setPreferredPoster(itemId, tag),
    onSuccess: (_result, tag) => {
      // The poster appears on the detail page, in every grid and on the home rails, so all of them are
      // stale now — as is the candidate list itself, whose "selected" marker just moved.
      for (const key of [["item-images", itemId], ["library-detail", itemId], ["library"], ["recent"], ["resume"], ["nextup"], ["collections"]]) {
        queryClient.invalidateQueries({ queryKey: key });
      }
      toast.success(tag === null ? "Poster choice cleared" : "Poster updated");
      onOpenChange(false);
    },
    onError: (error) => toast.error("Couldn’t change the poster", { description: errorMessage(error) }),
  });

  const posters = (images.data ?? []).filter((image) => image.type === "Primary");
  const pinned = posters.find((poster) => poster.pinned);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-3xl">
        <DialogHeader>
          <DialogTitle>Choose a poster</DialogTitle>
          <DialogDescription>
            The poster shown for <span className="text-foreground font-medium">{title}</span> everywhere in
            the library. Without a choice here the server picks the best-ranked one for your language.
          </DialogDescription>
        </DialogHeader>

        {images.isPending && <p className="text-muted-foreground py-8 text-center text-sm">Loading artwork…</p>}
        {images.isError && (
          <p className="text-muted-foreground py-8 text-center text-sm">Couldn’t load this title’s artwork.</p>
        )}
        {!images.isPending && !images.isError && posters.length === 0 && (
          <p className="text-muted-foreground py-8 text-center text-sm">
            This title has no cached posters yet. Refresh its metadata and try again.
          </p>
        )}

        {posters.length > 0 && (
          <ul className="grid max-h-[60vh] grid-cols-3 gap-3 overflow-y-auto sm:grid-cols-4">
            {posters.map((poster) => (
              <li key={poster.tag}>
                <button
                  type="button"
                  disabled={pin.isPending}
                  onClick={() => pin.mutate(poster.tag)}
                  aria-pressed={poster.selected}
                  className={cn(
                    "group focus-visible:ring-ring relative block w-full overflow-hidden rounded-md focus-visible:ring-2 focus-visible:outline-none",
                    poster.selected ? "ring-brand ring-2" : "hover:opacity-90",
                  )}
                >
                  {/* eslint-disable-next-line @next/next/no-img-element */}
                  <img src={poster.url} alt="" className="aspect-[2/3] w-full object-cover" />
                  {poster.selected && (
                    <span className="bg-brand text-brand-foreground absolute top-1.5 right-1.5 flex size-5 items-center justify-center rounded-full">
                      <Check className="size-3.5" aria-hidden />
                    </span>
                  )}
                  {/* The language of the text on the poster is the whole point of the choice, so it is
                      spelled out rather than left to the eye — "No text" is what makes a sequel ambiguous. */}
                  <span className="bg-background/80 text-foreground absolute inset-x-0 bottom-0 truncate px-1.5 py-1 text-[11px]">
                    {poster.language ? poster.language.toUpperCase() : "No text"}
                    {poster.pinned && " · pinned"}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}

        <DialogFooter>
          {/* Only offered when there is a choice to undo: "reset" with nothing pinned would do nothing. */}
          {pinned && (
            <Button variant="ghost" disabled={pin.isPending} onClick={() => pin.mutate(null)}>
              <RotateCcw />
              Use the automatic choice
            </Button>
          )}
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            Close
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
