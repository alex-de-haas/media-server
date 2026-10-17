import { defineConfig, devices } from "@playwright/test";
import base from "./playwright.config";

// Focused Safari regression coverage; install with `pnpm exec playwright install webkit`.
export default defineConfig({
  ...base,
  projects: [{ name: "webkit", testMatch: "watch-time.spec.ts", use: { ...devices["Desktop Safari"] } }],
});
