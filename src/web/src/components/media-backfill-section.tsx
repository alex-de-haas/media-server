"use client";

import { useMutation, useQuery } from "@tanstack/react-query";
import { Loader2, RefreshCw } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type MediaBackfillReport } from "@/lib/media-server";
import { errorMessage } from "@/lib/ui";
import { useSession } from "@/components/app-shell";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

/**
 * Fills in media data that could not be read when it was first written: sources probed while the transcode
 * engine was detached, and sidecar tracks placed before their codec, channels and sample rate were recorded.
 *
 * An explicit action rather than something that fires when the engine reconnects — rewriting stored data on
 * its own the moment a dependency reappears would be a surprise, and a probe is fast enough to just run.
 */
export function MediaBackfillSection() {
  const { role } = useSession();
  const isAdmin = role === "admin";

  // Everything this does is read through the engine's probe, which exists only while the engine is
  // attached. Without it the pass would re-read the same files with the header parser and change nothing,
  // so the control says why instead of offering a no-op.
  const { data: transcode } = useQuery({
    queryKey: ["transcode-availability"],
    queryFn: () => mediaServer.transcodeAvailability(),
    staleTime: 5 * 60 * 1000,
    enabled: isAdmin,
  });
  const engineAttached = transcode?.available ?? false;

  const backfill = useMutation({
    mutationFn: () => mediaServer.backfillMedia(),
    onSuccess: (report: MediaBackfillReport) => {
      const filled = report.itemsRefreshed + report.sidecarsFilled;
      if (filled === 0) {
        toast.success("Nothing to fill in", { description: "Every file already has engine-read media data." });
        return;
      }

      toast.success("Media data filled in", {
        description: [
          report.itemsRefreshed > 0 ? `${report.itemsRefreshed} title(s) re-probed` : null,
          report.sidecarsFilled > 0 ? `${report.sidecarsFilled} sidecar track(s) filled` : null,
        ]
          .filter(Boolean)
          .join(" · "),
      });
    },
    onError: (error) => toast.error("Couldn’t fill in media data", { description: errorMessage(error) }),
  });

  if (!isAdmin) {
    return null;
  }

  const report = backfill.data;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Media data</CardTitle>
        <CardDescription>
          Re-reads the files that were probed while the transcode engine was detached, and fills in the codec,
          channels and sample rate of separate audio and subtitle files that were added before those were
          recorded. Track names and languages are left alone.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3 text-sm">
        <div>
          <Button
            variant="secondary"
            size="sm"
            disabled={backfill.isPending || !engineAttached}
            onClick={() => backfill.mutate()}
          >
            {backfill.isPending ? <Loader2 className="animate-spin" /> : <RefreshCw />}
            {backfill.isPending ? "Reading files…" : "Fill in media data"}
          </Button>
        </div>

        {!engineAttached && (
          <p className="text-muted-foreground text-xs">
            Needs the transcode engine — it is what reads the files. Without it nothing here can be answered
            that wasn’t already.
          </p>
        )}

        {report && (
          <p className="text-muted-foreground text-xs">
            {report.itemsRefreshed} title(s) re-probed, {report.sidecarsFilled} sidecar track(s) filled.
            {report.remaining > 0 && (
              <>
                {" "}
                {report.remaining} source(s) still without engine data — most likely their catalog root isn’t
                bound into the engine.
              </>
            )}
          </p>
        )}
      </CardContent>
    </Card>
  );
}
