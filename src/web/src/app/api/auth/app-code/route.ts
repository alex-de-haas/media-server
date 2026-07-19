import { createAppCodeRouteHandler } from "@hosty-sdk/app/server";
import { hostyAppConfig } from "@/lib/host-auth";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

// Exchanges the one-time Shell/Core launch code for the app identity cookie and returns the
// token so the browser can keep the bearer fallback for when the cross-site cookie is blocked
// (providers.tsx stores it in sessionStorage).
export const POST = createAppCodeRouteHandler(hostyAppConfig);
