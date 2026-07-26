"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Heart, Trash2 } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type RemovedTitle } from "@/lib/media-server";
import { errorMessage } from "@/lib/ui";
import { formatTimeAgo } from "@/lib/format";
import { useSession } from "@/components/app-shell";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
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

/**
 * The window onto ghosts: titles deleted from the library whose watch history and favorites were
 * kept. The watched calendar shows only plays, so a favorited-but-never-watched removed title is
 * visible nowhere else — this list is where it can be unfavorited, or purged for good (admin).
 */
export function RemovedTitlesSection() {
  const { role } = useSession();
  const queryClient = useQueryClient();
  const [purgeTarget, setPurgeTarget] = useState<RemovedTitle | null>(null);

  const titles = useQuery({ queryKey: ["removed-titles"], queryFn: mediaServer.listRemovedTitles });
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["removed-titles"] });

  const unfavorite = useMutation({
    mutationFn: (id: string) => mediaServer.clearRemovedFavorite(id),
    onSuccess: () => {
      invalidate();
      toast.success("Favorite cleared");
    },
    onError: (error) => toast.error("Couldn’t clear favorite", { description: errorMessage(error) }),
  });

  const purge = useMutation({
    mutationFn: (id: string) => mediaServer.purgeRemovedTitle(id),
    onSuccess: () => {
      setPurgeTarget(null);
      invalidate();
      toast.success("Removed title deleted for good");
    },
    onError: (error) => toast.error("Couldn’t delete removed title", { description: errorMessage(error) }),
  });

  // The section only appears when there is something to manage — an empty ghost list is noise.
  if (!titles.data?.length) {
    return null;
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Removed titles</CardTitle>
        <CardDescription>
          Deleted from the library, but their watch history and favorites were kept. A re-added title
          picks its history back up automatically.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-2 text-sm">
        {titles.data.map((title) => (
          <div key={title.id} className="flex items-center gap-3 rounded-md border p-2">
            <div className="bg-secondary h-14 w-10 shrink-0 overflow-hidden rounded">
              {title.posterUrl && (
                // eslint-disable-next-line @next/next/no-img-element
                <img src={title.posterUrl} alt="" className="h-full w-full object-cover" />
              )}
            </div>
            <div className="min-w-0 flex-1">
              <p className="truncate font-medium">
                {title.title}
                {title.year != null && <span className="text-muted-foreground font-normal"> ({title.year})</span>}
              </p>
              <p className="text-muted-foreground flex flex-wrap items-center gap-x-2 text-xs">
                <Badge variant="secondary">{title.kind}</Badge>
                {title.isFavorite && (
                  <span className="inline-flex items-center gap-1">
                    <Heart className="size-3 fill-current" aria-hidden /> Favorite
                  </span>
                )}
                {title.playCount > 0 && (
                  <span>
                    {title.playCount} {title.playCount === 1 ? "play" : "plays"}
                    {title.lastWatchedAt && ` · last ${formatTimeAgo(title.lastWatchedAt)}`}
                  </span>
                )}
                <span>removed {formatTimeAgo(title.removedAt)}</span>
              </p>
            </div>
            {title.isFavorite && (
              <Button
                variant="ghost"
                size="sm"
                disabled={unfavorite.isPending}
                onClick={() => unfavorite.mutate(title.id)}
              >
                Unfavorite
              </Button>
            )}
            {role === "admin" && (
              <Button
                variant="ghost"
                size="icon-sm"
                aria-label={`Delete ${title.title} permanently`}
                disabled={purge.isPending}
                onClick={() => setPurgeTarget(title)}
              >
                <Trash2 />
              </Button>
            )}
          </div>
        ))}
      </CardContent>

      <AlertDialog open={purgeTarget !== null} onOpenChange={(open) => !open && setPurgeTarget(null)}>
        <AlertDialogContent className="sm:max-w-md">
          <AlertDialogHeader>
            <AlertDialogTitle>Delete permanently?</AlertDialogTitle>
            <AlertDialogDescription>
              Erase <span className="text-foreground font-medium">{purgeTarget?.title}</span> together
              with its watch history and favorites, for every user. This cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel size="sm">Cancel</AlertDialogCancel>
            <AlertDialogAction
              variant="destructive"
              size="sm"
              onClick={() => purgeTarget && purge.mutate(purgeTarget.id)}
            >
              Delete permanently
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </Card>
  );
}
