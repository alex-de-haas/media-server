"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState } from "react";
import { useAppCodeExchange } from "@/components/use-app-code-exchange";
import { TooltipProvider } from "@/components/ui/tooltip";
import { Toaster } from "@/components/ui/sonner";

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
