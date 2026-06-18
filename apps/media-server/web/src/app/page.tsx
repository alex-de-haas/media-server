"use client";

import { useQuery } from "@tanstack/react-query";
import { apiJson, ApiError } from "@/lib/api";
import { Dashboard } from "@/components/dashboard";

interface Session {
  userId: string;
  email: string | null;
  displayName: string | null;
  role: "admin" | "user";
}

export default function Home() {
  const session = useQuery({
    queryKey: ["session"],
    queryFn: () => apiJson<Session>("/api/auth/session"),
    retry: false,
  });

  if (session.isSuccess) {
    return <Dashboard />;
  }

  const unauthenticated =
    session.isError && session.error instanceof ApiError && session.error.status === 401;

  return (
    <main className="mx-auto flex max-w-3xl flex-col gap-4 p-6">
      <h1 className="text-2xl font-semibold tracking-tight">Media Server</h1>
      <p className="text-muted-foreground text-sm">
        {unauthenticated
          ? "No active session. Open this app from the Hosty Shell to authenticate."
          : "Loading…"}
      </p>
    </main>
  );
}
