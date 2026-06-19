import { AlertTriangle, Check, Loader2, Pause } from "lucide-react";
import { cn } from "@/lib/utils";

// The canonical pipeline order, mirroring the server's `IngestStage` enum
// (Intake → Identify → Download → Organize → Probe → Enrich → Publish).
const STAGES = ["Intake", "Identify", "Download", "Organize", "Probe", "Enrich", "Publish"] as const;

// What the active step is doing, when the caller knows more than the coarse ingest `status`
// (e.g. the Download stage maps to a torrent that may be actively transferring or paused).
export type StepActivity = "running" | "paused";

// `done`/`pending` are self-explanatory; `active` is the current step sitting idle (queued); `running`
// and `paused` annotate the current step's live activity; `attention` is NeedsReview / Failed.
type StepKind = "done" | "running" | "paused" | "active" | "attention" | "pending";

/**
 * Compact horizontal stepper embedded in each ingest card. Completed stages collapse to a check, the
 * active stage is highlighted (spinner when running, pause glyph when paused), and a stage that needs
 * the user (NeedsReview / Failed) turns amber.
 */
export function IngestStepper({
  stage,
  stagesCompleted,
  status,
  activity,
  className,
}: {
  stage: string;
  stagesCompleted: string[];
  status: string;
  activity?: StepActivity;
  className?: string;
}) {
  const allDone = status === "Done";
  const currentIndex = STAGES.findIndex((s) => s === stage);
  const completed = new Set(stagesCompleted);

  function kindFor(index: number): StepKind {
    if (allDone || completed.has(STAGES[index]) || index < currentIndex) return "done";
    if (index === currentIndex) {
      if (status === "Failed" || status === "NeedsReview") return "attention";
      if (activity === "paused") return "paused";
      if (activity === "running" || status === "Running") return "running";
      return "active";
    }
    return "pending";
  }

  return (
    <ol className={cn("flex items-start", className)}>
      {STAGES.map((label, index) => {
        const kind = kindFor(index);
        const last = index === STAGES.length - 1;
        return (
          <li key={label} className={cn("flex items-start", !last && "flex-1")}>
            <div className="flex flex-col items-center gap-1">
              <StepNode kind={kind} index={index} />
              <span className={cn("text-[10px] leading-none whitespace-nowrap", labelClass(kind))}>{label}</span>
            </div>
            {!last && (
              <span aria-hidden className={cn("mt-[9px] h-0.5 flex-1 rounded-full", kind === "done" ? "bg-primary" : "bg-border")} />
            )}
          </li>
        );
      })}
    </ol>
  );
}

function labelClass(kind: StepKind): string {
  switch (kind) {
    case "attention":
      return "font-medium text-amber-600 dark:text-amber-500";
    case "running":
    case "active":
      return "text-foreground font-medium";
    case "paused":
      return "text-muted-foreground font-medium";
    default:
      return "text-muted-foreground";
  }
}

function StepNode({ kind, index }: { kind: StepKind; index: number }) {
  return (
    <span
      className={cn(
        "flex size-5 items-center justify-center rounded-full border text-[10px] font-medium",
        kind === "done" && "border-primary bg-primary text-primary-foreground",
        (kind === "running" || kind === "active") && "border-primary bg-primary/10 text-primary",
        kind === "paused" && "border-muted-foreground/50 bg-muted text-muted-foreground border-dashed",
        kind === "attention" && "border-amber-500 bg-amber-500/15 text-amber-600 dark:text-amber-500",
        kind === "pending" && "border-border text-muted-foreground",
      )}
    >
      {kind === "done" ? (
        <Check className="size-3" />
      ) : kind === "attention" ? (
        <AlertTriangle className="size-3" />
      ) : kind === "paused" ? (
        <Pause className="size-3" />
      ) : kind === "running" ? (
        <Loader2 className="size-3 animate-spin" />
      ) : (
        index + 1
      )}
    </span>
  );
}
