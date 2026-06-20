// Infuse library deep links, built from TMDb ids. Infuse resolves the id against the user's already-
// connected Jellyfin source, so no token or direct stream URL is needed (the web UI has neither). Library
// deep links require Infuse 8.4.7+. See https://support.firecore.com/hc/en-us/articles/215090997.

export type InfuseTarget =
  | { kind: "movie"; tmdbId: string | null }
  | { kind: "series"; tmdbId: string | null }
  | { kind: "episode"; seriesTmdbId: string | null; season: number | null; episode: number | null };

/** Returns the `infuse://…` deep link, or null when the ids needed to build it are missing. */
export function infuseDeepLink(target: InfuseTarget, options: { play?: boolean } = {}): string | null {
  const suffix = options.play ? "?play" : "";
  switch (target.kind) {
    case "movie":
      return target.tmdbId ? `infuse://movie/${target.tmdbId}${suffix}` : null;
    case "series":
      return target.tmdbId ? `infuse://series/${target.tmdbId}${suffix}` : null;
    case "episode":
      return target.seriesTmdbId != null && target.season != null && target.episode != null
        ? `infuse://series/${target.seriesTmdbId}-${target.season}-${target.episode}${suffix}`
        : null;
  }
}

/**
 * Launches an Infuse deep link. The Shell embeds us in a sandboxed iframe with `allow-popups` but NOT
 * `allow-top-navigation`, so a custom scheme must go through `window.open`, not a top-level navigation.
 */
export function openInfuse(deepLink: string): void {
  window.open(deepLink, "_blank");
}
