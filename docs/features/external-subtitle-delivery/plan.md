# External Subtitle Delivery — plan

Status: On Hold
Created: 2026-07-27
Updated: 2026-07-27

Parked deliberately. The capability is recorded here rather than as a remark
inside another document, so that it is visible in the index and can be picked up
without re-deriving it. Nothing here is scheduled.

## Goal

Let a sidecar subtitle file reach a client without being merged into the video.

[external-track-sidecars](../external-track-sidecars/feature.md) keeps mapped
subtitles as files next to the library file and can merge them on request. Until
they are merged they are invisible over the API — which is a real gap for
subtitles specifically, because both Jellyfin and Infuse have a standard mechanism
for them and this app simply does not implement it.

The asymmetry with audio is the whole point of this document existing separately.
Infuse cannot use external audio tracks by any route — they must be inside the
container
([Firecore community](https://community.firecore.com/t/support-for-external-audio-tracks/15848/10))
— so for audio, merging is the only answer. Subtitles are the case where a sidecar
can stay a sidecar and still be usable.

## Target behavior

- `MediaStreamDto` gains the delivery URL Jellyfin clients expect. The DTO
  already carries `IsExternal`, `SupportsExternalStream` and `DeliveryMethod`
  (set to `"External"` for external subtitles by `JellyfinItemMapper`), but no
  URL, so a client is told a stream exists and given no way to fetch it.
- A delivery endpoint in the Jellyfin surface serves the file, following
  Jellyfin's route shape
  (`/Videos/{itemId}/{mediaSourceId}/Subtitles/{index}/Stream.{format}`). No such
  route exists today.
- Sidecar subtitles then work end to end without a merge, and merging becomes a
  convenience for people who want a single file rather than the only way to use
  the track.

## Deliverables

- [ ] **Delivery endpoint** in the Jellyfin surface, resolving the external path
      through the catalog sandbox rather than trusting the stored path.
- [ ] **`DeliveryUrl` on `MediaStreamDto`**, populated for external subtitle
      streams.
- [ ] **Format handling** — at minimum passthrough of the stored file; conversion
      between subtitle formats is explicitly not in scope.
- [ ] **Unit tests** covering the route, the sandbox check, and the DTO.
- [ ] **Docs.** `feature.md` for this folder; update
      `jellyfin-compatibility/feature.md`.
- [ ] **Version bump** — new functionality, so a minor bump while the app is `0.x`.

## Open questions

- Which subtitle formats are worth serving directly, and does anything need
  converting for Infuse specifically?
- Does the endpoint need the same authentication treatment as the video stream
  route, or is it covered by the existing Jellyfin auth path?

## Verification steps

1. Place a sidecar subtitle beside a library file and confirm it appears in
   `PlaybackInfo` with `DeliveryMethod: "External"` and a working URL.
2. Fetch the URL and confirm the file is served unchanged.
3. Confirm a path outside the catalog is refused.
4. Play the item in Infuse and confirm the subtitle is selectable and renders.
