"use client";

import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Heart, Star, Trash2 } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type RemovedTitle } from "@/lib/media-server";
import { errorMessage } from "@/lib/ui";
import { formatTimeAgo } from "@/lib/format";
import { useSession } from "@/components/app-shell";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

/**
 * What is left of a title after its files are gone: the marks this user made on it. A removed title has
 * no detail page — nothing about it can be played, edited or browsed — so its card opens this instead.
 *
 * Clearing the last of those marks ends the title for good, on the server's side: nothing is keeping the
 * row alive any more, so it is purged rather than left as a ghost nobody can reach. That is why the
 * clears say what they will cost.
 */
export function RemovedTitleDialog({
  title,
  onOpenChange,
}: {
  title: RemovedTitle | null;
  onOpenChange: (open: boolean) => void;
}) {
  const { role } = useSession();
  const queryClient = useQueryClient();
  const [confirmPurge, setConfirmPurge] = useState(false);

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ["removed-titles"] });
    queryClient.invalidateQueries({ queryKey: ["watch-history-calendar"] });
  };

  const unfavorite = useMutation({
    mutationFn: (id: string) => mediaServer.clearRemovedFavorite(id),
    onSuccess: () => {
      invalidate();
      onOpenChange(false);
      toast.success("Favorite cleared");
    },
    onError: (error) => toast.error("Couldn’t clear favorite", { description: errorMessage(error) }),
  });

  // Its own action rather than part of the unfavorite: deleting a file does not retract a verdict on a
  // film that was watched, so a rating survives the removal and only goes when the user says so.
  const unrate = useMutation({
    mutationFn: (id: string) => mediaServer.clearRemovedRating(id),
    onSuccess: () => {
      invalidate();
      onOpenChange(false);
      toast.success("Rating cleared");
    },
    onError: (error) => toast.error("Couldn’t clear rating", { description: errorMessage(error) }),
  });

  const purge = useMutation({
    mutationFn: (id: string) => mediaServer.purgeRemovedTitle(id),
    onSuccess: () => {
      setConfirmPurge(false);
      invalidate();
      onOpenChange(false);
      toast.success("Removed title deleted for good");
    },
    onError: (error) => toast.error("Couldn’t delete removed title", { description: errorMessage(error) }),
  });

  return (
    <>
      <Dialog open={title !== null} onOpenChange={onOpenChange}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader>
            <DialogTitle>
              {title?.title}
              {title?.year != null && <span className="text-muted-foreground font-normal"> ({title.year})</span>}
            </DialogTitle>
            <DialogDescription>
              Removed from the library {title && formatTimeAgo(title.removedAt)}. Its files are gone; what
              you marked on it is kept, and comes back with the title if it is ever re-added.
            </DialogDescription>
          </DialogHeader>

          {title && (
            <div className="flex flex-col gap-3 text-sm">
              <div className="flex flex-wrap items-center gap-2">
                <Badge variant="secondary">{title.kind}</Badge>
                {title.isFavorite && (
                  <span className="text-muted-foreground inline-flex items-center gap-1 text-xs">
                    <Heart className="size-3 fill-current" aria-hidden /> Favorite
                  </span>
                )}
                {title.userRating != null && (
                  <span className="text-muted-foreground inline-flex items-center gap-1 text-xs">
                    <Star className="size-3 fill-current" aria-hidden /> {title.userRating}/5
                  </span>
                )}
                {title.playCount > 0 && (
                  <span className="text-muted-foreground text-xs">
                    {title.playCount} {title.playCount === 1 ? "play" : "plays"}
                    {title.lastWatchedAt && ` · last ${formatTimeAgo(title.lastWatchedAt)}`}
                  </span>
                )}
              </div>

              <div className="flex flex-wrap gap-2">
                {title.isFavorite && (
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={unfavorite.isPending}
                    onClick={() => unfavorite.mutate(title.id)}
                  >
                    Unfavorite
                  </Button>
                )}
                {title.userRating != null && (
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={unrate.isPending}
                    onClick={() => unrate.mutate(title.id)}
                  >
                    Clear rating
                  </Button>
                )}
                {role === "admin" && (
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={purge.isPending}
                    onClick={() => setConfirmPurge(true)}
                  >
                    <Trash2 />
                    Delete permanently
                  </Button>
                )}
              </div>

              {title.playCount > 0 && (
                <p className="text-muted-foreground text-xs">
                  Its plays stay on the Watched calendar, where they can be deleted one at a time.
                </p>
              )}
            </div>
          )}
        </DialogContent>
      </Dialog>

      <AlertDialog open={confirmPurge} onOpenChange={setConfirmPurge}>
        <AlertDialogContent className="sm:max-w-md">
          <AlertDialogHeader>
            <AlertDialogTitle>Delete permanently?</AlertDialogTitle>
            <AlertDialogDescription>
              Erase <span className="text-foreground font-medium">{title?.title}</span> together with its
              watch history and favorites, for every user. This cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel size="sm">Cancel</AlertDialogCancel>
            <AlertDialogAction
              variant="destructive"
              size="sm"
              onClick={() => title && purge.mutate(title.id)}
            >
              Delete permanently
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}
