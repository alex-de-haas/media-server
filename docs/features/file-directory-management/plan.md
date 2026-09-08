# File and Directory Management

Status: Draft
Created: 2026-09-08
Updated: 2026-09-08

## Goal

Decide what a leaf item is once its last version is gone.

`DELETE /api/library/sources/{id}` drops one `MediaSource`; when it was the item's
only one, the movie or episode stays in the library as a row with no file behind
it. The detail page then reads "No media sources available", the episode row
([episode-media](../episode-media/feature.md)) reads "No file", and the title keeps
appearing in grids, rails and the Jellyfin surface as something that can be played. Removing the item itself behaves differently: it tombstones
or purges the row and prunes an emptied season and series
([feature.md](feature.md#removal-semantics)).

Parity between movies and episodes is kept in episode-media on purpose; the
surprising part is that the row survives at all, and that is a question of
removal semantics rather than of any one surface — so it is shaped here.

## Target behavior

Not decided. The candidates, written as a diff against `feature.md`:

- **Prune on the last version**, the way an episode delete already prunes an
  emptied season: removing the last source removes the item through the same
  tombstone-or-purge path as a whole-item delete, with the same
  `deleteUserData` choice, and the API reports what else went.
- **Keep the row, but say so everywhere**: the item stays as an explicit "no
  file" state — hidden from playback surfaces (Jellyfin, native, rails) and
  marked in the grids — so a version deleted by mistake can be re-added by a
  scan without losing the item's history.
- **Refuse**: the last version cannot be removed through the version control at
  all; the operator deletes the item instead, which already offers the file and
  history choices.

## Open questions

1. Which of the three, and does the catalog scan's missing-file rule
   ([catalog-maintenance](../catalog-maintenance/feature.md)) already answer part
   of it — an item whose row has no source is not an item whose file went
   missing, so the scan does not ghost it today.
2. Whether the answer is the same for a movie and an episode: pruning an episode
   can cascade to its season and series, which a movie has no counterpart for.

## Deliverables

None until the target behaviour is chosen.

## Verification

To be written with the deliverables.
