---
status: In Progress
created: 2026-10-08
updated: 2026-10-08
summary: Adopt the shared SDK overlay for identity recovery, required permissions and protected content visibility.
components: [src/web]
---

# Hosty Overlay Adoption

## Approved Scope

The owner approved migration and a separate pull request on 2026-10-08. Replace the
legacy identity bridge with HostyOverlay and add its standard readiness endpoint.
Keep application session APIs, role mapping, cookies, code exchange and data access
unchanged. The implementation depends on SDK 0.22.0 from
[Hosty PR #559](https://github.com/alex-de-haas/docker-host/pull/559).

## Deliverables

- [x] D1. Mount HostyOverlay around protected content and expose authenticated, role-appropriate setup readiness while retaining application session behavior.
- [x] D2. Verify route/proxy regressions, unit tests, lint, production build and Core-managed browser login in standalone and Shell-embedded modes.
- [ ] D3. After SDK 0.22.0 is published, regenerate the registry lockfile, verify a frozen registry installation and finalize feature documentation/index.

## Verification

Use the SDK package built from PR #559 for candidate validation before publication.
A candidate build does not establish registry availability. Keep this PR a draft
until D3 passes; do not merge before the SDK release. Bump the runtime app version
with this migration. Browser acceptance uses an isolated Core with ordinary
password login and disposable data.

## Candidate Results

Ordinary password sign-in passes both in a standalone browser tab and inside the
Core-managed Shell iframe. The QA host uses isolated users/data and a loopback
Core origin distinct from app origins. Local candidate unit tests, lint and
production build pass. Hosty manifest and documentation validation pass.

179 unit tests and 159 mocked-BFF Playwright tests pass with Next.js 16.3.8.
Core-managed acceptance runs the actual web/API services with an empty disposable
library. Only the QA manifest relaxes the external torrent dependency and optional
media configuration requirements; production dependencies remain unchanged.
No .NET or Apple source changed, so their full test suites were not rerun.

D3 remains open: npm still publishes 0.21.2. The checked-in registry lockfile cannot
resolve the new ^0.22.0 requirement yet; CI's frozen install is expected to remain
blocked until publication and lockfile regeneration. No tarball, local path or
invented registry integrity is committed.
