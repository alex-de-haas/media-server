"use client";

import { useEffect } from "react";

// Adopts the Hosty Shell's light/dark theme inside the embedded iframe. The Shell pushes its
// resolved theme over two channels: initial `?hosty_theme`/`?hosty_theme_preference` URL params and
// a `hosty:shell-theme` postMessage (after load and on every change). Ported from the verified
// reference (docker-host/apps/demo-app/src/components/HostThemeBridge.tsx). The app ships both token
// sets in globals.css and follows the host — it has no theme toggle of its own.

type HostyResolvedTheme = "light" | "dark";
type HostyThemePreference = "light" | "dark" | "system";

const resolvedThemeStorageKey = "hosty.theme.resolved";
const themePreferenceStorageKey = "hosty.theme.preference";

export function HostThemeBridge() {
  useEffect(() => {
    const urlTheme = getUrlTheme();
    const storedTheme = getStoredTheme();
    let explicitTheme = Boolean(urlTheme || storedTheme);

    if (urlTheme) {
      applyTheme(urlTheme, getUrlThemePreference() ?? urlTheme, true);
    } else if (storedTheme) {
      applyTheme(storedTheme, getStoredThemePreference() ?? storedTheme, true);
    } else {
      applyTheme(getSystemTheme(), "system", false);
    }
    cleanUrlThemeParams();

    const systemThemeQuery = window.matchMedia("(prefers-color-scheme: dark)");
    const handleSystemThemeChange = () => {
      if (!explicitTheme) {
        applyTheme(getSystemTheme(), "system", false);
      }
    };

    const expectedParentOrigin = getExpectedParentOrigin();
    const handleMessage = (event: MessageEvent) => {
      if (window.parent === window || event.source !== window.parent) {
        return;
      }

      if (expectedParentOrigin && event.origin !== expectedParentOrigin) {
        return;
      }

      const data = event.data;
      if (!data || typeof data !== "object" || (data as { type?: unknown }).type !== "hosty:shell-theme") {
        return;
      }

      const theme = normalizeResolvedTheme((data as { theme?: unknown }).theme);
      if (!theme) {
        return;
      }

      explicitTheme = true;
      applyTheme(theme, normalizeThemePreference((data as { preference?: unknown }).preference) ?? theme, true);
    };

    systemThemeQuery.addEventListener("change", handleSystemThemeChange);
    window.addEventListener("message", handleMessage);

    return () => {
      systemThemeQuery.removeEventListener("change", handleSystemThemeChange);
      window.removeEventListener("message", handleMessage);
    };
  }, []);

  return null;
}

function applyTheme(theme: HostyResolvedTheme, preference: HostyThemePreference, persist: boolean) {
  const root = document.documentElement;
  root.classList.toggle("dark", theme === "dark");
  root.style.colorScheme = theme;
  root.dataset.hostyTheme = theme;
  root.dataset.hostyThemePreference = preference;

  if (persist) {
    window.sessionStorage.setItem(resolvedThemeStorageKey, theme);
    window.sessionStorage.setItem(themePreferenceStorageKey, preference);
  } else {
    window.sessionStorage.removeItem(resolvedThemeStorageKey);
    window.sessionStorage.removeItem(themePreferenceStorageKey);
  }
}

function getUrlTheme() {
  return normalizeResolvedTheme(new URL(window.location.href).searchParams.get("hosty_theme"));
}

function getUrlThemePreference() {
  return normalizeThemePreference(new URL(window.location.href).searchParams.get("hosty_theme_preference"));
}

function getStoredTheme() {
  return normalizeResolvedTheme(window.sessionStorage.getItem(resolvedThemeStorageKey));
}

function getStoredThemePreference() {
  return normalizeThemePreference(window.sessionStorage.getItem(themePreferenceStorageKey));
}

function getSystemTheme(): HostyResolvedTheme {
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

function normalizeResolvedTheme(value: unknown): HostyResolvedTheme | null {
  return value === "light" || value === "dark" ? value : null;
}

function normalizeThemePreference(value: unknown): HostyThemePreference | null {
  return value === "light" || value === "dark" || value === "system" ? value : null;
}

function cleanUrlThemeParams() {
  const url = new URL(window.location.href);
  const hadThemeParams = url.searchParams.has("hosty_theme") || url.searchParams.has("hosty_theme_preference");
  if (!hadThemeParams) {
    return;
  }

  url.searchParams.delete("hosty_theme");
  url.searchParams.delete("hosty_theme_preference");
  window.history.replaceState(null, "", `${url.pathname}${url.search}${url.hash}`);
}

function getExpectedParentOrigin() {
  if (!document.referrer) {
    return null;
  }

  try {
    const referrerOrigin = new URL(document.referrer).origin;
    return referrerOrigin === window.location.origin ? null : referrerOrigin;
  } catch {
    return null;
  }
}
