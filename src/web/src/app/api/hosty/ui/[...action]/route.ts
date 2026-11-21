import { createHostyUiRouteHandler } from "@hosty-sdk/app/server";
import { hostyAppConfig } from "@/lib/host-auth";

export const dynamic = "force-dynamic";
export const runtime = "nodejs";

const handler = createHostyUiRouteHandler(hostyAppConfig);
export { handler as GET, handler as POST };
