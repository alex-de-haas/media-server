"use client";

import { useEffect, useRef, useState } from "react";
import { setBearerToken } from "@/lib/api";
import { clearRecoveryGuard } from "@/components/session-recovery";

/**
 * On first load inside the Shell iframe the URL carries a one-time `?code`. Exchange it for an
 * identity token (which sets the app-origin cookie and returns the token for the bearer
 * fallback), then strip the code from the URL. Children render only after this completes so the
 * first session query carries credentials.
 */
export function useAppCodeExchange(): boolean {
  const [ready, setReady] = useState(false);
  const exchangeRef = useRef<Promise<void> | null>(null);

  useEffect(() => {
    let cancelled = false;

    // Strict Mode replays effects. Share the single-use exchange, but let each
    // effect subscribe with its own cancellation flag so the live one reveals the page.
    exchangeRef.current ??= (async () => {
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
    })();

    void exchangeRef.current.then(() => {
      if (!cancelled) {
        setReady(true);
      }
    });

    return () => {
      cancelled = true;
    };
  }, []);

  return ready;
}
