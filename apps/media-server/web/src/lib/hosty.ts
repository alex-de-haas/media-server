// Server-only view over the HOSTY_* environment Hosty Core injects into the `web` service.
// Read at request time inside route handlers (Node runtime) — never imported by client code.

export interface HostyServerEnv {
  /** Process-to-Core origin used for app-code exchange and identity revalidation. */
  coreOrigin: string;
  /** Browser-facing Core origin (for links back to the Shell), if provided. */
  corePublicOrigin: string | null;
  /** This app's stable id; the identity token audience must match it. */
  appId: string;
  /** Service token (bearer) for Core's internal/app APIs; absent when run standalone. */
  serviceToken: string | null;
  /**
   * Internal base URL of the `api` service. Injected by Core because `web` declares
   * `dependsOn: ["api"]` (intra-app service discovery): docker → `http://api:8080` over the
   * per-app network, dev → `http://localhost:{assigned}` over loopback.
   */
  apiUrl: string;
}

export function hostyServerEnv(): HostyServerEnv {
  return {
    coreOrigin: process.env.HOSTY_CORE_ORIGIN ?? "http://localhost:3001",
    corePublicOrigin: process.env.HOSTY_CORE_PUBLIC_ORIGIN ?? null,
    appId: process.env.HOSTY_APP_ID ?? "com.haas.media-server",
    serviceToken: process.env.HOSTY_APP_SERVICE_TOKEN ?? null,
    apiUrl: (process.env.HOSTY_SERVICE_API_URL ?? "http://localhost:8080").replace(/\/+$/, ""),
  };
}
