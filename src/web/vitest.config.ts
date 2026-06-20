import { defineConfig } from "vitest/config";
import { fileURLToPath } from "node:url";

export default defineConfig({
  test: {
    environment: "node",
    include: ["src/**/*.test.ts"],
  },
  resolve: {
    // Mirror the tsconfig `@/*` path alias so tests can import modules that use it.
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
});
