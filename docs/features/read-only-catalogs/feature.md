---
created: 2026-10-14
updated: 2026-10-14
summary: Immutable catalog imports and explicit transcode exports in separate operator-configured output mounts.
components: [src/api, src/web]
---

# Read-only catalogs and transcode exports

A catalog can declare `readOnly: true` when created. The flag is persisted, returned by the catalog API and MCP catalog listing, and shown in Catalogs. Existing catalogs remain writable. Read-only mode is immutable through the settings API.

The root must already exist. Creation and scanning do not create `.incoming`. The organizer adopts confirmed files at their original relative paths; it does not rename or move them. Companion placement does not rename files in a read-only catalog. Explicit manual identification does not require the original-title lookup used to choose writable library folder names; enrichment still uses the configured metadata provider. Metadata, probe results, watch state and caches remain in app data.

Server-side guards reject torrent intake, file deletion, renaming, remapping, moves into or out of the catalog, staged retargeting, extraction, joining and disc conversion that write beside inputs. The HTTP response is 409 with an explanation. Removing database entries without deleting files remains available. Torrent and move destination pickers omit read-only catalogs.

The flag complements the operator's read-only Hosty/Docker mount. It is an application policy, not a replacement for an OS-enforced read-only bind.

## Explicit output folders

The optional `transcodeOutput` external mount slot provides named writable output roots through `HOSTY_MOUNT_TRANSCODEOUTPUT`. Configure the same global mount label in the dedicated Transcode Engine's media bindings. Input mounts retain their existing labels and can remain read-only in both apps.

`GET /api/transcode/destinations` lists output labels that do not overlap catalog mounts or reuse their labels. `POST /api/transcode` accepts optional `outputMountLabel`. Without it, the existing writable-catalog behavior creates a sibling library version. Read-only inputs require an explicit output mount.

Each export owns `<output root>/<job ID>/<generated filename>.mkv`. Only configured labels and job-owned paths are accepted. Traversal, symbolic-link ancestors, overlapping mount roots and another job's directory are rejected. Export ownership is recorded before engine submission, using the job ID for idempotent engine submission.

The completed export is probed and remains a job result; it is not attached as a second library version or a duplicate title in another catalog. Completed exports survive engine restarts through the persisted Media Server job record. Completion promotion can retry after a Media Server restart.

## Operator controls

Remux and Convert dialogs offer **Output destination**. Their ordinary version destination is labelled for writable catalogs; export destinations are explicitly named. Job cards identify exports and offer **Download export** and **Delete export**. Deletion asks for confirmation and removes only the owned output file and an empty job directory. It preserves the original source and unrelated files.

The admin-only download route is `GET /api/transcode/{id}/output`. `DELETE /api/transcode/{id}?deleteOutput=true` removes a terminal export and its job; ownership is retained if cleanup fails. Active jobs must be cancelled and reach a terminal state first. Dismissing a job without `deleteOutput=true` intentionally keeps its output file.

Output mounts are external data: deleting a Hosty sandbox does not imply permission to erase an arbitrary shared mount. Disposable tests use a dedicated temporary output directory, remove job outputs before finishing, and explicitly clean the fixture directory afterwards.

## Verified local acceptance

The 2026-10-14 real Harness/browser run uses a fresh child Core, Media Server 0.92.0 built from this worktree and a dedicated Transcode Engine 0.13.0. It creates a read-only catalog, scans 31 files and manually identifies one file through the normal API without a metadata-provider key. The full 47m38s source exports as H.264 to the separate output mount in 399.058 seconds; independent full decoding is clean. Read-only conversion without an export destination returns 409 before creating a job.

Provider stop/start preserves the first export and a new remux completes in 4.604 seconds. Both results remain outside the source library, are downloadable and are deleted through normal Media controls before Harness Finish. The remux retains an original AVI duplicate-DTS diagnostic; original and remux decoders exit zero, and variable-frame-rate decoding is clean. The source library stays at one version.

After finishing both child Cores, all 118 source-file SHA-256 hashes and 154 filesystem entries match their pre-run state (94,222,024,758 bytes hashed). The coordinator then removes its remaining fixture resources. API tests: 2,237 passed; web tests: 199 passed; API/web Docker builds and manifest validation passed. This acceptance uses explicit manual test metadata; automatic TMDb enrichment and torrent ingestion are not covered. Output mounts remain externally owned and require explicit export/fixture cleanup.

## Testing Expectations

Cover default writable migrations, read-only creation without staging, missing roots, in-place scans and organization, rejection before database/file changes, and metadata-only removal. Cover explicit input/output mount separation, durable job ownership, no extra library version, successful output download/removal, overlap and symlink rejection, and isolation between job directories.

Run backend and web suites, production builds and manifest/documentation validation. Exercise normal Core setup/password authentication, browser installation and a real Harness worker against Docker read-only inputs. Compare file inventories and SHA-256 hashes before and after encoding, provider restart and complete sandbox cleanup.
