---
status: In Progress
created: 2026-11-09
updated: 2026-11-09
summary: Administrators can select another user's viewing history in the Watched calendar.
components: [src/api, src/web]
---

# Administrator viewing history

## Goal

Extend the existing [Watched calendar](feature.md) with an administrator-only
user selector. The default remains the signed-in user's own history. Selecting
another user shows that user's dated plays, undated marks and latest watched
month in both the grid and phone agenda.

Other users' history is read-only: deletion and time corrections remain limited
to the caller's own entries. The server checks administrator authority before
resolving a requested user; ordinary users cannot list users or read foreign
history by constructing a request. Keep the existing personal/native behavior.

## Deliverables

- [x] D1. Add an administrator-only user list and authorized user selection for both calendar read endpoints, with denied, missing and self-selection cases covered.
- [x] D2. Add a labelled user selector to Watched mode, isolate cached queries by user, reset open dialogs on selection changes, and hide foreign-history mutation controls.
- [ ] D3. Verify backend authorization and browser behavior, update the feature documentation and index, and prepare the runtime app's minor version increment.

## Verification

- Run affected xUnit tests, frontend tests, lint and production build.
- Cover switching between self and another user, undated history, month changes,
  read-only foreign day details, and the absence of the selector for ordinary users.
- Use a Core-managed child with separate test users/data for identity and runtime
  checks; export results before finishing the environment.

Verified on 2026-11-09: 123 watch-history xUnit cases (24 new route cases), 222
frontend unit tests, frontend lint and typecheck, frontend production build,
backend Release build including native OpenAPI generation, manifest validation
and documentation validation pass. Five new browser scenarios are included in
the 21 discovered calendar tests. Browser execution and app login remain pending
the child's ordinary installation approval; D3 stays open until those checks run.

## Publication

Commit, publication, merge and live-instance activation require separate operator
authorization.
