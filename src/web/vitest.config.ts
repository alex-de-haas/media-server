import { defineConfig } from "vitest/config";
import { fileURLToPath } from "node:url";

export default defineConfig({
  test: {
    environment: "node",
    include: ["src/**/*.test.ts"],
    server: {
      deps: {
        // Route the SDK through the transform pipeline: externalized packages load via
        // native Node and would bypass the `server-only` alias below.
        inline: ["@hosty-sdk/app"],
      },
    },
  },
  resolve: {
    // Mirror the tsconfig `@/*` path alias so tests can import modules that use it.
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
      // The real `server-only` throws outside a React Server Component environment by
      // design; tests exercise the SDK's server slice directly, so stub it out.
      "server-only": fileURLToPath(new URL("./test/server-only-stub.ts", import.meta.url)),
    },
  },
});
