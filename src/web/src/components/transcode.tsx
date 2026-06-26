"use client";

import { useId, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Trash2, X } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type LibraryMediaSource, type TranscodeJob } from "@/lib/media-server";
import { formatBytes, formatEta, formatPercent } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Field, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Progress } from "@/components/ui/progress";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

const CODECS = [
  { value: "hevc", label: "HEVC (H.265) — smaller files" },
  { value: "h264", label: "H.264 — most compatible" },
];

const HARDWARE = [
  { value: "auto", label: "Auto (GPU if available)" },
  { value: "vaapi", label: "VAAPI — GPU" },
  { value: "none", label: "Software — CPU" },
];

const ACTIVE_STATES = ["Queued", "Running"];

export function isTranscodeActive(job: TranscodeJob): boolean {
  return ACTIVE_STATES.includes(job.state);
}

/** Dialog to start a transcode of one movie source into a new, smaller version. */
export function TranscodeDialog({
  source,
  open,
  onOpenChange,
}: {
  source: LibraryMediaSource;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const queryClient = useQueryClient();
  const codecId = useId();
  const hardwareId = useId();
  const crfId = useId();
  const [codec, setCodec] = useState("hevc");
  const [hardware, setHardware] = useState("auto");
  const [crf, setCrf] = useState("");

  const convert = useMutation({
    mutationFn: () =>
      mediaServer.createTranscodeJob({
        sourceId: source.id,
        videoCodec: codec,
        hardwareAcceleration: hardware,
        // CRF only applies to software encoding; the backend ignores it otherwise.
        crf: hardware === "none" && crf.trim() ? Number(crf) : null,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["transcode-jobs"] });
      onOpenChange(false);
      toast.success("Transcode started", { description: "The smaller version will appear here when it’s ready." });
    },
    onError: (error) => toast.error("Couldn’t start transcode", { description: errorMessage(error) }),
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Convert version</DialogTitle>
          <DialogDescription>
            Re-encode this source into a new, smaller version of the movie. The original stays until you delete it.
          </DialogDescription>
        </DialogHeader>

        <form
          className="flex flex-col gap-3 text-sm"
          onSubmit={(e) => {
            e.preventDefault();
            convert.mutate();
          }}
        >
          <p className="text-muted-foreground text-xs">
            Source: <span className="font-mono">{source.container}</span> · {formatBytes(source.sizeBytes)}
            {source.versionName ? ` · ${source.versionName}` : ""}
          </p>

          <Field>
            <FieldLabel htmlFor={codecId}>Codec</FieldLabel>
            <Select value={codec} onValueChange={(value) => setCodec((value as string | null) ?? "hevc")} items={CODECS}>
              <SelectTrigger id={codecId} className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {CODECS.map((option) => (
                  <SelectItem key={option.value} value={option.value}>
                    {option.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>

          <Field>
            <FieldLabel htmlFor={hardwareId}>Encoder</FieldLabel>
            <Select value={hardware} onValueChange={(value) => setHardware((value as string | null) ?? "auto")} items={HARDWARE}>
              <SelectTrigger id={hardwareId} className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {HARDWARE.map((option) => (
                  <SelectItem key={option.value} value={option.value}>
                    {option.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>

          {hardware === "none" && (
            <Field>
              <FieldLabel htmlFor={crfId}>Quality (CRF, optional)</FieldLabel>
              <Input
                id={crfId}
                inputMode="numeric"
                placeholder="e.g. 23 — lower is better quality"
                value={crf}
                onChange={(e) => setCrf(e.target.value.replace(/[^0-9]/g, ""))}
              />
              <p className="text-muted-foreground text-xs">0–51. Leave blank for the encoder default.</p>
            </Field>
          )}

          <DialogFooter className="mt-2">
            <Button type="button" variant="ghost" size="sm" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" size="sm" disabled={convert.isPending}>
              {convert.isPending ? "Starting…" : "Start convert"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** One transcode job row: live progress while running, an outcome + dismiss once terminal. */
export function TranscodeJobRow({ job }: { job: TranscodeJob }) {
  const queryClient = useQueryClient();
  const active = isTranscodeActive(job);
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["transcode-jobs"] });

  const cancel = useMutation({
    mutationFn: () => mediaServer.cancelTranscodeJob(job.id),
    onSuccess: invalidate,
    onError: (error) => toast.error("Couldn’t cancel", { description: errorMessage(error) }),
  });
  const remove = useMutation({
    mutationFn: () => mediaServer.removeTranscodeJob(job.id),
    onSuccess: invalidate,
    onError: (error) => toast.error("Couldn’t dismiss", { description: errorMessage(error) }),
  });

  return (
    <div className="flex flex-col gap-1">
      <div className="flex items-center justify-between gap-2">
        <span className="min-w-0 truncate font-mono text-xs" title={job.name ?? job.outputPath}>
          {job.name ?? job.outputPath}
        </span>
        <div className="flex shrink-0 items-center gap-2">
          <span className={cn(job.state === "Failed" ? "text-destructive" : "text-muted-foreground", "text-xs")}>
            {stateLabel(job)}
          </span>
          {active ? (
            <Button variant="ghost" size="icon-sm" aria-label="Cancel" disabled={cancel.isPending} onClick={() => cancel.mutate()}>
              <X />
            </Button>
          ) : (
            <Button variant="ghost" size="icon-sm" aria-label="Dismiss" disabled={remove.isPending} onClick={() => remove.mutate()}>
              <Trash2 />
            </Button>
          )}
        </div>
      </div>
      {active && <Progress value={Math.min(job.percentComplete, 100)} />}
      {job.state === "Failed" && job.error && <p className="text-destructive text-xs">{job.error}</p>}
    </div>
  );
}

function stateLabel(job: TranscodeJob): string {
  if (job.state === "Running") {
    const parts = [formatPercent(job.percentComplete)];
    if (job.speed) parts.push(`${job.speed.toFixed(1)}×`);
    if (job.etaSeconds != null) parts.push(`ETA ${formatEta(job.etaSeconds)}`);
    return parts.join(" · ");
  }
  if (job.state === "Queued") return "Queued";
  if (job.state === "Completed") return job.outputSizeBytes ? `Done · ${formatBytes(job.outputSizeBytes)}` : "Done";
  return job.state;
}
