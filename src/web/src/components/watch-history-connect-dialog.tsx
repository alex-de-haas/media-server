"use client";

import { useEffect, useRef, useState } from "react";
import { ExternalLink } from "lucide-react";
import { mediaServer, type WatchHistoryAuthorization } from "@/lib/media-server";
import { errorMessage } from "@/lib/ui";
import { Button, buttonVariants } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Spinner } from "@/components/ui/spinner";

// The default cadence if the provider does not name one; Trakt's device flow asks for ~5s.
const DEFAULT_POLL_MS = 5000;

type Phase =
  | { kind: "starting" }
  | { kind: "pending"; userCode: string; verificationUrl: string; expiresAt: string | null }
  | { kind: "error"; message: string };

/**
 * Runs a provider's Device OAuth flow for the signed-in user: start it, show the activation code and
 * verification URL, then poll at the provider's cadence until the user approves it on the provider's
 * site. Nothing here ever sees a token — the poll simply reports Approved once Core has stored one.
 *
 * The polling loop lives in an effect keyed to a single attempt so it starts once, backs off on
 * SlowDown, and is torn down the moment the dialog closes or the component unmounts.
 */
export function WatchHistoryConnectDialog({
  providerKey,
  providerName,
  open,
  onOpenChange,
  onConnected,
}: {
  providerKey: string;
  providerName: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onConnected: () => void;
}) {
  const [phase, setPhase] = useState<Phase>({ kind: "starting" });
  // Bumped each time the dialog opens so the effect restarts a fresh attempt.
  const [attempt, setAttempt] = useState(0);

  // Held in a ref so a parent re-render that hands us new callback identities does not restart the
  // device flow and request a fresh code out from under the user.
  const callbacks = useRef({ onConnected, onOpenChange });
  useEffect(() => {
    callbacks.current = { onConnected, onOpenChange };
  });

  // Reset to a fresh attempt on each (re)open, in render as React documents rather than in an effect.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setPhase({ kind: "starting" });
      setAttempt((value) => value + 1);
    }
  }

  useEffect(() => {
    if (!open || attempt === 0) {
      return;
    }

    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const schedule = (delayMs: number) => {
      timer = setTimeout(poll, delayMs);
    };

    const poll = async () => {
      let result: WatchHistoryAuthorization;
      try {
        result = await mediaServer.pollWatchHistoryAuthorization(providerKey);
      } catch (error) {
        if (!cancelled) {
          setPhase({ kind: "error", message: errorMessage(error) });
        }
        return;
      }
      if (cancelled) {
        return;
      }
      switch (result.state) {
        case "Approved":
          callbacks.current.onConnected();
          callbacks.current.onOpenChange(false);
          return;
        case "Denied":
          setPhase({ kind: "error", message: `You declined the request on ${providerName}.` });
          return;
        case "Expired":
          setPhase({ kind: "error", message: "The activation code expired before it was approved." });
          return;
        case "SlowDown":
        case "Pending":
        default:
          schedule((result.pollIntervalSeconds ?? DEFAULT_POLL_MS / 1000) * 1000);
      }
    };

    const start = async () => {
      let prompt: WatchHistoryAuthorization;
      try {
        prompt = await mediaServer.startWatchHistoryAuthorization(providerKey);
      } catch (error) {
        if (!cancelled) {
          setPhase({ kind: "error", message: errorMessage(error) });
        }
        return;
      }
      if (cancelled) {
        return;
      }
      if (!prompt.userCode || !prompt.verificationUrl) {
        setPhase({ kind: "error", message: "The provider did not return an activation code." });
        return;
      }
      setPhase({
        kind: "pending",
        userCode: prompt.userCode,
        verificationUrl: prompt.verificationUrl,
        expiresAt: prompt.expiresAt,
      });
      schedule((prompt.pollIntervalSeconds ?? DEFAULT_POLL_MS / 1000) * 1000);
    };

    void start();
    return () => {
      cancelled = true;
      if (timer) {
        clearTimeout(timer);
      }
    };
  }, [open, attempt, providerKey, providerName]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Connect {providerName}</DialogTitle>
          <DialogDescription>
            Approve this device on {providerName} to link your account. This window updates on its own
            once you have.
          </DialogDescription>
        </DialogHeader>

        {phase.kind === "starting" && (
          <div className="flex items-center gap-2 text-sm text-muted-foreground">
            <Spinner /> Requesting an activation code…
          </div>
        )}

        {phase.kind === "pending" && (
          <div className="flex flex-col gap-4 text-sm">
            <div className="flex flex-col gap-1">
              <span className="text-muted-foreground">Enter this code on {providerName}:</span>
              <span className="font-mono text-2xl tracking-[0.3em] tabular-nums">{phase.userCode}</span>
            </div>
            <a
              href={phase.verificationUrl}
              target="_blank"
              rel="noreferrer noopener"
              className={buttonVariants({ variant: "secondary", className: "w-fit" })}
            >
              <ExternalLink /> Open {providerName}
            </a>
            <div className="flex items-center gap-2 text-muted-foreground">
              <Spinner /> Waiting for you to approve it…
            </div>
          </div>
        )}

        {phase.kind === "error" && (
          <p className="text-sm text-destructive">{phase.message}</p>
        )}

        <DialogFooter>
          {phase.kind === "error" ? (
            <>
              <Button variant="ghost" size="sm" onClick={() => onOpenChange(false)}>
                Close
              </Button>
              <Button
                size="sm"
                onClick={() => {
                  setPhase({ kind: "starting" });
                  setAttempt((value) => value + 1);
                }}
              >
                Try again
              </Button>
            </>
          ) : (
            <Button variant="ghost" size="sm" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
