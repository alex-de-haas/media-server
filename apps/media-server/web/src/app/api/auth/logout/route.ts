import { NextResponse } from "next/server";
import { IDENTITY_COOKIE } from "@/lib/host-auth";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

/** Clears the app-origin identity cookie. The browser also drops its bearer fallback. */
export async function POST() {
  const response = NextResponse.json({ ok: true });
  response.cookies.delete(IDENTITY_COOKIE);
  return response;
}
