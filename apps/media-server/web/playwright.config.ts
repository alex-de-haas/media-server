import { defineConfig, devices } from "@playwright/test";

// E2e runs against a production build with the BFF/identity layer mocked at the network boundary
// (see e2e/support.ts) — the app needs Hosty Core for real identity, which CI doesn't have, and the
// project policy is to validate host-facing behavior through Core, not forged tokens.
const PORT = 3100;

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? "github" : "list",
  use: {
    baseURL: `http://localhost:${PORT}`,
    trace: "on-first-retry",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: `pnpm build && pnpm exec next start --port ${PORT}`,
    url: `http://localhost:${PORT}`,
    timeout: 180_000,
    reuseExistingServer: !process.env.CI,
  },
});
