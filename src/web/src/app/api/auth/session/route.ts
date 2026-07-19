import { NextRequest, NextResponse } from "next/server";
import {
  readIdentityToken,
  readSessionRecoveryParams,
  resolveHostSession,
  type SessionFailureStatus,
} from "@/lib/host-auth";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

// One HTTP status per failure class so the client recovery flow can tell recoverable (401) from
// terminal (403) from transient (503) from operator error (500) without parsing prose.
const FAILURE_RESPONSES: Record<SessionFailureStatus, { status: number; error: string }> = {
  "not-present": { status: 401, error: "unauthenticated" },
  expired: { status: 401, error: "unauthenticated" },
  forbidden: { status: 403, error: "forbidden" },
  unavailable: { status: 503, error: "core_unavailable" },
  misconfigured: { status: 500, error: "misconfigured" },
};

/**
 * Returns the current Host identity, revalidated against Core. Failures carry the recovery
 * parameters (app id + browser-reachable Core origin) so the client can run session recovery.
 * They ride in this response — not in a server-component prop — because the app's pages are
 * prerendered at image build time, where the HOSTY_* environment does not exist yet; this route
 * is force-dynamic, so the values are read on the machine that actually runs the app. Neither
 * value is secret: both are addressed to the browser by design.
 */
export async function GET(request: NextRequest) {
  const resolution = await resolveHostSession(readIdentityToken(request));

  if (resolution.status !== "active") {
    const failure = FAILURE_RESPONSES[resolution.status];
    return NextResponse.json(
      { error: failure.error, recovery: readSessionRecoveryParams() },
      { status: failure.status, headers: { "cache-control": "no-store" } },
    );
  }

  return NextResponse.json(resolution.session, { headers: { "cache-control": "no-store" } });
}
