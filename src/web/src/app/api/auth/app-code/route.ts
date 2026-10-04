import { createAppCodeRouteHandler } from "@hosty-sdk/app/server";
import { hostyAppConfig } from "@/lib/host-auth";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

// Exchanges the one-time Core authorization code for the app identity cookie and returns the
// token so the browser can keep the bearer fallback for when the cross-site cookie is blocked
// (the SDK retains this grant in memory only).
export const POST = createAppCodeRouteHandler(hostyAppConfig);
