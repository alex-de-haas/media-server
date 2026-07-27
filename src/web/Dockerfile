# syntax=docker/dockerfile:1
# Build context: src/web (self-contained pnpm project for the `web` service).
# Node major is kept on the active LTS line and in step with `node-version` in ci.yml,
# so the image runs what CI actually builds and tests against.
#
# Base pinned by digest for reproducible builds, matching how this repo pins its GitHub Actions.
# To update, re-pin with:
#   docker buildx imagetools inspect node:24-bookworm-slim --format '{{.Manifest.Digest}}'

FROM node:24-bookworm-slim@sha256:6f7b03f7c2c8e2e784dcf9295400527b9b1270fd37b7e9a7285cf83b6951452d AS base
WORKDIR /app
ENV NEXT_TELEMETRY_DISABLED=1
# Pin pnpm via corepack so installs match CI (no packageManager field in package.json).
RUN corepack enable && corepack prepare pnpm@11.7.0 --activate

# ---- deps ----
FROM base AS deps
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile

# ---- build ----
FROM base AS build
ENV NODE_ENV=production
# Opt into Next standalone output for the image (kept off by default so `next start` works elsewhere).
ENV NEXT_OUTPUT_STANDALONE=1
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN pnpm build

# ---- runner ----
FROM node:24-bookworm-slim@sha256:6f7b03f7c2c8e2e784dcf9295400527b9b1270fd37b7e9a7285cf83b6951452d AS runner
WORKDIR /app
ENV NODE_ENV=production
ENV NEXT_TELEMETRY_DISABLED=1
ENV PORT=3000
ENV HOSTNAME=0.0.0.0

# Next.js standalone output: a minimal server plus its traced node_modules.
# --chown stamps ownership as each layer is written, so the unprivileged `node` user owns the bundle
# without a `chown -R` pass (which would rewrite every file's metadata and duplicate the tree into an
# extra layer). Only the `api` service mounts the app data directory, so `web` needs no privileged
# startup step and can drop to `node` outright.
COPY --from=build --chown=node:node /app/.next/standalone ./
COPY --from=build --chown=node:node /app/.next/static ./.next/static
COPY --from=build --chown=node:node /app/public ./public
# Next's image optimizer writes into <distDir>/cache at runtime.
RUN install -d -o node -g node /app/.next/cache

USER node
EXPOSE 3000
CMD ["node", "server.js"]
