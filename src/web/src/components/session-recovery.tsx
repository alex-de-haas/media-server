"use client";

import { useEffect, useState } from "react";
import { buildCoreOpenUrl } from "@/lib/identity";
import type { SessionFailureStatus } from "@/lib/host-auth";

// Once-per-tab guard so a standalone tab that comes back from Core still unauthorized does not
// bounce through /open forever. Cleared by the code exchange in providers.tsx on success.
const RECOVERY_GUARD_KEY = "hosty.auth.recovery-attempted";
// How long an embedded frame waits for Shell to reissue a launch code before falling back to the
// manual sign-in card (i.e. it is embedded by something other than Hosty Shell, or Shell broke).
const EMBEDDED_RECOVERY_TIMEOUT_MS = 4_000;

/** Allows the next standalone recovery to auto-redirect again after a successful sign-in. */
export function clearRecoveryGuard(): void {
  try {
    window.sessionStorage.removeItem(RECOVERY_GUARD_KEY);
  } catch {
    // sessionStorage may be blocked; recovery still works, only the once-per-tab guard is lost.
  }
}

function readRecoveryGuard(): boolean {
  try {
    return window.sessionStorage.getItem(RECOVERY_GUARD_KEY) === "1";
  } catch {
    return false;
  }
}

function writeRecoveryGuard(): void {
  try {
    window.sessionStorage.setItem(RECOVERY_GUARD_KEY, "1");
  } catch {
    // Without the guard a broken exchange could redirect repeatedly; accept that trade when
    // storage is blocked rather than losing recovery entirely.
  }
}

// The expired flow progresses from silent recovery to the manual card; every other failure
// status renders as a pure function of the prop and needs no state at all.
type ExpiredPhase =
  | { kind: "recovering" }
  | { kind: "signin"; openUrl: string | null; embedded: boolean };

/**
 * Session recovery per the platform UX contract: embedded in an authenticated Shell the user
 * never sees an auth screen — the frame posts `hosty:auth-required` and Shell silently reissues
 * a launch code (swapping the iframe src, which reloads this document with a fresh `?code`).
 * Standalone redirects straight to Core `/open`, which bounces through `/login` and returns
 * with a fresh code — at most once per tab. Cards render only in fallback and terminal states.
 */
export function SessionRecovery({
  status,
  appId,
  corePublicOrigin,
}: {
  status: SessionFailureStatus;
  appId: string;
  corePublicOrigin: string | null;
}) {
  const [expired, setExpired] = useState<ExpiredPhase>({ kind: "recovering" });

  useEffect(() => {
    if (status !== "expired") {
      return;
    }

    const openUrl = buildCoreOpenUrl(corePublicOrigin, appId, window.location);

    if (window.self !== window.top) {
      // Embedded: the iframe sandbox forbids top navigation, so ask Shell to reissue a code.
      // The payload carries no secret, so targetOrigin "*" is safe — Shell verifies the sender
      // (source window, frame origin, app id) before acting.
      try {
        window.parent.postMessage({ type: "hosty:auth-required", appId }, "*");
      } catch {
        // Ignore; the timeout below still falls back to the manual sign-in card.
      }
      const timeoutId = window.setTimeout(
        () => setExpired({ kind: "signin", openUrl, embedded: true }),
        EMBEDDED_RECOVERY_TIMEOUT_MS,
      );
      return () => window.clearTimeout(timeoutId);
    }

    // Standalone: auto-recover once per tab. A null openUrl means the redirect is known to be
    // impossible (no browser-reachable Core origin from this page), so fall through to the card
    // instead of stranding the user on a dead redirect.
    if (openUrl && !readRecoveryGuard()) {
      writeRecoveryGuard();
      window.location.assign(openUrl);
      return;
    }
    const timeoutId = window.setTimeout(() => setExpired({ kind: "signin", openUrl, embedded: false }), 0);
    return () => window.clearTimeout(timeoutId);
  }, [status, appId, corePublicOrigin]);

  return (
    <main className="mx-auto flex max-w-3xl flex-col gap-4 p-6">
      <h1 className="text-2xl font-semibold tracking-tight">Media Server</h1>
      {status === "denied" ? (
        <p className="text-muted-foreground text-sm">
          You are signed in to Hosty but are not allowed to use this app.
        </p>
      ) : status === "unavailable" ? (
        <>
          <p className="text-muted-foreground text-sm">Can&rsquo;t reach Hosty right now.</p>
          <button type="button" onClick={() => window.location.reload()} className={actionClassName}>
            Retry
          </button>
        </>
      ) : status === "misconfigured" ? (
        <p className="text-muted-foreground text-sm">
          This app is not configured correctly on the host. Contact the administrator.
        </p>
      ) : expired.kind === "recovering" ? (
        <p className="text-muted-foreground text-sm">Reconnecting to Hosty…</p>
      ) : (
        <>
          <p className="text-muted-foreground text-sm">Your Hosty session ended.</p>
          {expired.openUrl ? (
            <a
              href={expired.openUrl}
              {...(expired.embedded ? { target: "_blank", rel: "noopener noreferrer" } : {})}
              className={actionClassName}
            >
              Sign in via Hosty
            </a>
          ) : (
            <p className="text-muted-foreground text-sm">
              Open this app from the machine running Hosty, or configure a public origin for
              remote access.
            </p>
          )}
        </>
      )}
    </main>
  );
}

const actionClassName =
  "bg-primary text-primary-foreground hover:bg-primary/90 inline-flex h-9 w-fit items-center justify-center rounded-md px-4 text-sm font-medium transition-colors";
