"use client";

import { createContext, useContext } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { Activity, Film, FolderTree, Home, Settings, Tv, type LucideIcon } from "lucide-react";
import { apiJson, ApiError } from "@/lib/api";
import { cn } from "@/lib/utils";
import { RealtimeBridge } from "@/components/realtime-bridge";

export interface Session {
  userId: string;
  email: string | null;
  displayName: string | null;
  role: "admin" | "user";
}

const SessionContext = createContext<Session | null>(null);

/** The validated Host identity for the current request. Throws outside <AppShell>. */
export function useSession(): Session {
  const session = useContext(SessionContext);
  if (!session) {
    throw new Error("useSession must be used within <AppShell>.");
  }
  return session;
}

/**
 * App-wide chrome: resolves the session once, gates rendering on it, and renders the top tab bar
 * around the active page. Catalogs is admin-only (gated here and enforced server-side via
 * `AppRoles.AdminPolicy`); Settings is available to every user (it holds the per-user Infuse credential).
 */
export function AppShell({ children }: { children: React.ReactNode }) {
  const session = useQuery({
    queryKey: ["session"],
    queryFn: () => apiJson<Session>("/api/auth/session"),
    retry: false,
  });

  if (session.isPending) {
    return <ShellMessage>Loading…</ShellMessage>;
  }

  if (session.isError) {
    const unauthenticated = session.error instanceof ApiError && session.error.status === 401;
    return (
      <ShellMessage>
        {unauthenticated
          ? "No active session. Open this app from the Hosty Shell to authenticate."
          : "Could not load your session. Try reloading."}
      </ShellMessage>
    );
  }

  return (
    <SessionContext.Provider value={session.data}>
      <RealtimeBridge />
      {/* overflow-x-clip lets full-bleed children (e.g. the detail backdrop) span 100vw without adding a
          horizontal scrollbar. `clip` (not `hidden`) doesn't create a scroll container, so the sticky
          TabBar below keeps sticking to the viewport. */}
      <div className="flex min-h-full flex-col overflow-x-clip">
        <TabBar isAdmin={session.data.role === "admin"} />
        <main className="mx-auto flex w-full max-w-5xl flex-col gap-6 p-6">{children}</main>
      </div>
    </SessionContext.Provider>
  );
}

function ShellMessage({ children }: { children: React.ReactNode }) {
  return (
    <main className="mx-auto flex max-w-3xl flex-col gap-4 p-6">
      <h1 className="text-2xl font-semibold tracking-tight">Media Server</h1>
      <p className="text-muted-foreground text-sm">{children}</p>
    </main>
  );
}

const PRIMARY_TABS: { href: string; label: string; icon: LucideIcon }[] = [
  { href: "/", label: "Home", icon: Home },
  { href: "/movies", label: "Movies", icon: Film },
  { href: "/series", label: "Series", icon: Tv },
  { href: "/activity", label: "Activity", icon: Activity },
];

function TabBar({ isAdmin }: { isAdmin: boolean }) {
  const pathname = usePathname();
  const isActive = (href: string) => (href === "/" ? pathname === "/" : pathname.startsWith(href));

  return (
    <header className="bg-background/80 sticky top-0 z-10 border-b backdrop-blur">
      <nav className="mx-auto flex w-full max-w-5xl items-center gap-5 overflow-x-auto px-4">
        <span className="border-b-2 border-transparent py-3 pr-1 font-semibold tracking-tight">Media Server</span>
        {PRIMARY_TABS.map((tab) => (
          <TabLink key={tab.href} href={tab.href} label={tab.label} icon={tab.icon} active={isActive(tab.href)} />
        ))}
        <span className="ml-auto flex items-center gap-5">
          {isAdmin && <TabLink href="/catalogs" label="Catalogs" icon={FolderTree} active={isActive("/catalogs")} />}
          <TabLink href="/settings" label="Settings" icon={Settings} active={isActive("/settings")} />
        </span>
      </nav>
    </header>
  );
}

function TabLink({ href, label, icon: Icon, active }: { href: string; label: string; icon: LucideIcon; active: boolean }) {
  return (
    <Link
      href={href}
      aria-current={active ? "page" : undefined}
      className={cn(
        "inline-flex items-center gap-1.5 border-b-2 py-3 text-sm font-medium whitespace-nowrap transition-colors",
        active
          ? "border-brand text-foreground"
          : "border-transparent text-muted-foreground hover:text-foreground",
      )}
    >
      <Icon className="size-4" aria-hidden />
      {label}
    </Link>
  );
}
