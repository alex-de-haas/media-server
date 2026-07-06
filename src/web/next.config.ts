import type { NextConfig } from "next";
import path from "node:path";

// Standalone output is only wanted for the docker runtime image (the Dockerfile sets this flag). Off by
// default so `next start` (used by the e2e harness and any non-docker run) keeps working without the
// "next start does not work with output: standalone" warning. The web service is self-contained, so the
// tracing root is this directory and the bundle lands at .next/standalone/server.js.
const standalone = process.env.NEXT_OUTPUT_STANDALONE === "1";

const nextConfig: NextConfig = {
  ...(standalone ? { output: "standalone", outputFileTracingRoot: path.join(__dirname) } : {}),
  // Dev-only: Next blocks cross-origin requests to dev resources (e.g. /_next/webpack-hmr) by default.
  // The app is reached over multiple loopback hosts during Hosty/iframe dev, so allow 127.0.0.1
  // alongside the auto-allowed localhost. Ignored in production builds.
  allowedDevOrigins: ["127.0.0.1"],
};

export default nextConfig;
