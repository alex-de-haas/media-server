import { createHostySessionRouteHandler } from "@hosty-sdk/app/server";
import { hostyAppConfig } from "@/lib/host-auth";

export const dynamic = "force-dynamic";
export const runtime = "nodejs";
export const GET = createHostySessionRouteHandler(hostyAppConfig);
