import { createAppCodeRouteHandler } from "@hosty-sdk/app/server";
import { hostyAppConfig } from "@/lib/host-auth";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

// Exchanges a code with its initiating proof and the app service identity. The SDK keeps
// embedded grants on this app origin for the tab, with an in-memory fallback when storage is blocked.
export const POST = createAppCodeRouteHandler(hostyAppConfig);
