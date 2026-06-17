import { NextRequest, NextResponse } from "next/server";
import { readIdentityToken, revalidateIdentity } from "@/lib/host-auth";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

/** Returns the current Host identity, revalidated against Core. */
export async function GET(request: NextRequest) {
  const token = readIdentityToken(request);
  if (!token) {
    return NextResponse.json({ error: "unauthenticated" }, { status: 401 });
  }

  const session = await revalidateIdentity(token);
  if (!session) {
    return NextResponse.json({ error: "unauthenticated" }, { status: 401 });
  }

  return NextResponse.json(session);
}
