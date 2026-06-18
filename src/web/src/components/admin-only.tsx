"use client";

import type { ReactNode } from "react";
import { useSession } from "@/components/app-shell";

/** Client-side guard for admin pages. The server enforces the real check; this keeps the UI honest. */
export function AdminOnly({ children }: { children: ReactNode }) {
  const { role } = useSession();
  if (role !== "admin") {
    return <p className="text-muted-foreground text-sm">This page is available to administrators only.</p>;
  }
  return <>{children}</>;
}
