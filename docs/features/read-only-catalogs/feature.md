---
created: 2026-10-14
updated: 2026-10-14
summary: Immutable catalog imports and explicit transcode exports in separate operator-configured output mounts.
components: [src/api, src/web]
---

# Read-only catalogs and transcode exports

A catalog can declare `readOnly: true` when created. The flag is persisted, returned by the catalog API and MCP catalog listing, and shown in Catalogs. Existing catalogs remain writable. Read-only mode is immutable through the settings API.

The root must already exist. Creation and scanning do not create `.incoming`. The organizer adopts confirmed files at their original relative paths; it does not rename or move them. Companions are probed and registered as external streams at their original paths, without renaming files. Explicit manual identification does not require the original-title lookup used to choose writable library folder names; enrichment still uses the configured metadata provider. Metadata, probe results, watch state and caches remain in app data.

Server-side guards reject torrent intake, file deletion, renaming, remapping, moves into or out of the catalog, staged retargeting, extraction, joining and disc conversion that write beside inputs. The HTTP response is 409 with an explanation. Removing database entries without deleting files remains available after their exports are removed or explicitly dismissed. Torrent and move destination pickers omit read-only catalogs.

The flag complements the operator's read-only Hosty/Docker mount. It is an application policy, not a replacement for an OS-enforced read-only bind.

## Explicit output folders

The optional `transcodeOutput` external mount slot provides named writable output roots through `HOSTY_MOUNT_TRANSCODEOUTPUT`. Configure the same global mount label in the dedicated Transcode Engine's media bindings. Input mounts retain their existing labels and can remain read-only in both apps.

`GET /api/transcode/destinations` lists output labels that do not overlap catalog mounts or reuse their labels. `POST /api/transcode` accepts optional `outputMountLabel`. Without it, the existing writable-catalog behavior creates a sibling library version. Read-only inputs require an explicit output mount.

Each export owns `<output root>/<job ID>/<generated filename>.mkv`. Only configured labels and job-owned paths are accepted. Traversal, symbolic-link ancestors, overlapping mount roots and another job's directory are rejected. Export ownership and the exact request are recorded before engine submission, using the job ID for idempotent engine submission. Exports require the `idempotentTranscodes` capability (Transcode Engine 0.14.0+); older engines still support ordinary writable-catalog conversions. Unconfirmed submissions are inspected and retried with the same ID. Cancellation is persisted and waits for the provider to confirm a terminal state before deletion.

The completed export is probed and remains a job result; it is not attached as a second library version or a duplicate title in another catalog. Completed exports survive engine restarts through the persisted Media Server job record. Completion promotion can retry after a Media Server restart.

## Operator controls

Remux and Convert dialogs offer **Output destination**. Their ordinary version destination is labelled for writable catalogs; export destinations are explicitly named. Job cards identify exports and offer **Download export** and **Delete export**. Deletion asks for confirmation and removes only the owned output file and an empty job directory. It preserves the original source and unrelated files.

The admin-only direct download route is `GET /api/transcode/{id}/output`. The browser requests a five-minute export-only ticket through authenticated `POST /api/transcode/{id}/download`, then streams the file through the BFF without cookies or buffering it as a Blob. Tickets are bound to the job and read methods, use a separate signing purpose from library playback and contain no Core grant. `DELETE /api/transcode/{id}?deleteOutput=true` removes a terminal export and its job; ownership is retained if cleanup fails. Active jobs must be cancelled and reach a terminal state first. Dismissing a job without `deleteOutput=true` intentionally keeps its output file.

Output mounts are external data: deleting a Hosty sandbox does not imply permission to erase an arbitrary shared mount. Disposable tests use a dedicated temporary output directory, remove job outputs before finishing, and explicitly clean the fixture directory afterwards.

## Testing Expectations

Cover default writable migrations, read-only creation without staging, missing roots, in-place scans and organization, rejection before database/file changes, and metadata-only removal. Cover explicit input/output mount separation, durable job ownership, no extra library version, successful output download/removal, overlap and symlink rejection, and isolation between job directories.

Run backend and web suites, production builds and manifest/documentation validation. Exercise normal Core setup/password authentication, browser installation and a real Harness worker against Docker read-only inputs. Compare file inventories and SHA-256 hashes before and after encoding, provider restart and complete sandbox cleanup.
