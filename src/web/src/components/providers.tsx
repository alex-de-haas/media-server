"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { setBearerToken } from "@/lib/api";
import { clearRecoveryGuard } from "@/components/session-recovery";
import { TooltipProvider } from "@/components/ui/tooltip";
import { Toaster } from "@/components/ui/sonner";

/**
 * On first load inside the Shell iframe the URL carries a one-time `?code`. Exchange it for an
 * identity token (which sets the app-origin cookie and returns the token for the bearer
 * fallback), then strip the code from the URL. Children render only after this completes so the
 * first session query carries credentials.
 */
function useAppCodeExchange(): boolean {
  const [ready, setReady] = useState(false);
  const started = useRef(false);

  useEffect(() => {
    if (started.current) {
      return;
    }
    started.current = true;

    let cancelled = false;

    void (async () => {
      const url = new URL(window.location.href);
      const code = url.searchParams.get("code");

      if (code) {
        try {
          const response = await fetch("/api/auth/app-code", {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({ code }),
            credentials: "include",
          });
          if (response.ok) {
            const data = (await response.json()) as { accessToken?: string };
            if (data.accessToken) {
              setBearerToken(data.accessToken);
            }
            // A fresh session means the next standalone recovery may auto-redirect again.
            clearRecoveryGuard();
          }
        } catch {
          // Ignore: the session query will surface the unauthenticated state.
        } finally {
          url.searchParams.delete("code");
          window.history.replaceState(null, "", url.toString());
        }
      }

      if (!cancelled) {
        setReady(true);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  return ready;
}

export function Providers({ children }: { children: React.ReactNode }) {
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: { retry: false, refetchOnWindowFocus: false, staleTime: 30_000 },
        },
      }),
  );
  const ready = useAppCodeExchange();

  return (
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>{ready ? children : null}</TooltipProvider>
      <Toaster />
    </QueryClientProvider>
  );
}
