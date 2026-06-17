import { NextRequest, NextResponse } from "next/server";
import { hostyServerEnv } from "@/lib/hosty";
import { IDENTITY_COOKIE, identityCookieOptions } from "@/lib/host-auth";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

interface TokenResponse {
  accessToken: string;
  expiresInSeconds: number;
}

/**
 * Exchanges the one-time Shell launch `code` for a Host identity token at Core, stores it in the
 * app-origin HttpOnly cookie, and returns the token so the browser can also keep it in memory /
 * sessionStorage as a bearer fallback when the cross-site cookie is blocked.
 */
export async function POST(request: NextRequest) {
  let code: string | undefined;
  try {
    ({ code } = (await request.json()) as { code?: string });
  } catch {
    // fall through to the missing-code response
  }

  if (!code) {
    return NextResponse.json({ error: "missing_code" }, { status: 400 });
  }

  const env = hostyServerEnv();

  let exchange: Response;
  try {
    exchange = await fetch(`${env.coreOrigin}/api/auth/apps/token`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ code }),
      cache: "no-store",
    });
  } catch {
    // Core unreachable (network/DNS) — a transient platform issue, not a client error.
    return NextResponse.json({ error: "core_unreachable" }, { status: 502 });
  }

  if (!exchange.ok) {
    return NextResponse.json({ error: "exchange_failed" }, { status: 401 });
  }

  let token: TokenResponse;
  try {
    token = (await exchange.json()) as TokenResponse;
  } catch {
    return NextResponse.json({ error: "invalid_core_response" }, { status: 502 });
  }

  if (typeof token?.accessToken !== "string" || token.accessToken.length === 0 ||
    typeof token?.expiresInSeconds !== "number" || token.expiresInSeconds <= 0) {
    return NextResponse.json({ error: "invalid_core_response" }, { status: 502 });
  }

  const response = NextResponse.json({
    accessToken: token.accessToken,
    expiresInSeconds: token.expiresInSeconds,
  });
  response.cookies.set(IDENTITY_COOKIE, token.accessToken, identityCookieOptions(request, token.expiresInSeconds));
  return response;
}
