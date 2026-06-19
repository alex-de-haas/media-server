"use client";

import { useEffect, useState } from "react";
import { Toaster as SonnerToaster } from "sonner";

// Follows the resolved light/dark that HostThemeBridge applies to <html> (it toggles the `dark`
// class), updating live when the Hosty Shell pushes a theme change.
function useResolvedTheme(): "light" | "dark" {
  const [theme, setTheme] = useState<"light" | "dark">("light");

  useEffect(() => {
    const read = () => setTheme(document.documentElement.classList.contains("dark") ? "dark" : "light");
    read();
    const observer = new MutationObserver(read);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["class"] });
    return () => observer.disconnect();
  }, []);

  return theme;
}

export function Toaster() {
  const theme = useResolvedTheme();
  return <SonnerToaster theme={theme} position="bottom-right" richColors closeButton />;
}
