import { NextRequest, NextResponse } from "next/server";
import { hostyServerEnv } from "@/lib/hosty";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

// Only a short-lived, export-scoped ticket crosses this route. Core grants stay in the normal BFF.
export async function GET(request: NextRequest, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params;
  const token = request.nextUrl.searchParams.get("token");
  if (!/^[a-f0-9-]{36}$/i.test(id) || !token || token.length > 256) {
    return new NextResponse(null, { status: 404 });
  }
  const headers = new Headers();
  for (const name of ["range", "if-range"]) {
    const value = request.headers.get(name);
    if (value) headers.set(name, value);
  }
  const upstream = await fetch(`${hostyServerEnv().apiUrl}/api/transcode/${encodeURIComponent(id)}/download?token=${encodeURIComponent(token)}`, {
    headers, cache: "no-store", redirect: "manual", signal: request.signal,
  });
  const responseHeaders = new Headers({ "cache-control": "private, no-store", "referrer-policy": "no-referrer" });
  for (const name of ["content-type", "content-disposition", "content-length", "content-range", "accept-ranges", "etag", "last-modified"]) {
    const value = upstream.headers.get(name);
    if (value) responseHeaders.set(name, value);
  }
  return new NextResponse(upstream.body, { status: upstream.status, headers: responseHeaders });
}
