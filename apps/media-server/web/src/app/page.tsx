"use client";

import { useQuery } from "@tanstack/react-query";
import { apiJson, ApiError } from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";

interface Session {
  userId: string;
  email: string | null;
  displayName: string | null;
  role: "admin" | "user";
}

interface ApiHealth {
  status: string;
}

interface ApiIdentity {
  id: number;
  hostUserId: string;
  email: string | null;
  displayName: string | null;
  role: string;
}

function StatusBadge({ ok, pending, label }: { ok: boolean; pending: boolean; label: string }) {
  if (pending) {
    return <Badge variant="secondary">checking…</Badge>;
  }
  return <Badge variant={ok ? "default" : "destructive"}>{label}</Badge>;
}

export default function Home() {
  const session = useQuery({
    queryKey: ["session"],
    queryFn: () => apiJson<Session>("/api/auth/session"),
  });

  const health = useQuery({
    queryKey: ["api-health"],
    queryFn: () => apiJson<ApiHealth>("/api/proxy/health"),
    enabled: session.isSuccess,
  });

  const identity = useQuery({
    queryKey: ["api-identity"],
    queryFn: () => apiJson<ApiIdentity>("/api/proxy/api/me"),
    enabled: session.isSuccess,
  });

  const unauthenticated =
    session.isError && session.error instanceof ApiError && session.error.status === 401;

  return (
    <main className="mx-auto flex max-w-3xl flex-col gap-6 p-6">
      <header className="flex flex-col gap-1">
        <h1 className="text-2xl font-semibold tracking-tight">Media Server</h1>
        <p className="text-muted-foreground text-sm">
          Milestone&nbsp;0 — services boot under Hosty, identity validates end to end.
        </p>
      </header>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center justify-between gap-2">
            Session
            <StatusBadge
              pending={session.isPending}
              ok={session.isSuccess}
              label={session.isSuccess ? "authenticated" : "signed out"}
            />
          </CardTitle>
          <CardDescription>Host identity revalidated by the web BFF against Core.</CardDescription>
        </CardHeader>
        <CardContent className="text-sm">
          {session.isSuccess ? (
            <dl className="grid grid-cols-[7rem_1fr] gap-y-1">
              <dt className="text-muted-foreground">User</dt>
              <dd>{session.data.displayName ?? session.data.userId}</dd>
              <dt className="text-muted-foreground">Email</dt>
              <dd>{session.data.email ?? "—"}</dd>
              <dt className="text-muted-foreground">Role</dt>
              <dd>{session.data.role}</dd>
            </dl>
          ) : unauthenticated ? (
            <p className="text-muted-foreground">
              No active session. Open this app from the Hosty Shell to authenticate.
            </p>
          ) : (
            <p className="text-muted-foreground">Loading…</p>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center justify-between gap-2">
            Backend health
            <StatusBadge
              pending={health.isPending && health.fetchStatus !== "idle"}
              ok={health.isSuccess && health.data.status === "ok"}
              label={health.isSuccess ? health.data.status : "unreachable"}
            />
          </CardTitle>
          <CardDescription>Proxied through the BFF to the internal api service.</CardDescription>
        </CardHeader>
        <CardContent className="text-sm">
          {identity.isSuccess ? (
            <p className="text-muted-foreground">
              api recognised the forwarded identity as app user #{identity.data.id} ({identity.data.role}).
            </p>
          ) : (
            <p className="text-muted-foreground">
              Awaiting an authenticated session to reach the backend.
            </p>
          )}
        </CardContent>
      </Card>
    </main>
  );
}
