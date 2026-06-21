---
name: run-app
description: Launch, start, open, or preview the Media Server app for local development. Use whenever asked to run/start/open/preview the app — it goes through Hosty Core, never bare dev servers or the Claude preview tool.
---

# Running the Media Server (Hosty dev runtime)

The Media Server is a Hosty runtime app (`com.haas.media-server`). It is **always**
launched through **Hosty Core** in dev — Core injects identity, catalog mounts, and the
`web`→`api` service URL. The two services (`next dev` for `web`, `dotnet run` for `api`)
run as **Core-managed child processes**.

**Do NOT** start the services standalone (`pnpm dev` / `dotnet run`) and **do NOT** use
the Claude preview tool (`preview_start` / `.claude/launch.json`) for this app. Bare
servers spawn a duplicate `next dev`, miss the `HOSTY_*` environment, and produce an
unauthenticated, non-functional UI.

## Run / open the app

From the repo root:

```bash
scripts/dev.sh <hosty-user-email>
```

This ensures Core and the app are running, then prints a fresh authenticated URL of the
form `http://localhost:<port>?code=<one-time-code>`. Open that URL in the browser. The
code is **single-use**, so re-run the script for a new session (do not cache the URL).

## Useful facts

- App id: `com.haas.media-server`. Core status: `hosty status` (Core `7070`, Shell `7171`).
- App state: `hosty apps list`. Logs: `hosty apps logs com.haas.media-server`.
- The `web` dev port is **Core-assigned (dynamic)**; `hosty apps open … --format url`
  resolves it — never hard-code it.
- Underlying commands (what `scripts/dev.sh` wraps):
  `hosty apps start com.haas.media-server` then
  `hosty apps open com.haas.media-server --user <email> --format url`.
- Full dev loop and platform contracts: `docs/features/implementation-plan.md`.
