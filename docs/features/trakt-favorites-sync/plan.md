# Trakt Favorites Sync — remaining work

Status: In Progress
Created: 2026-07-26
Updated: 2026-07-26

The feature ships and is described in [feature.md](feature.md). One deliverable
from the original plan is not done, and this document exists only to hold it —
unfinished work belongs in an unchecked deliverable, not in a note inside a
completed feature document.

## Deliverables

- [ ] **Live contract verification against the dedicated Trakt test account.**
      Every Trakt call in this feature is exercised against a stub, so the
      payload shapes (`/sync/favorites` request bodies, `favorited_at`,
      `list.item_count`) and the HTTP 420 path are implemented from Trakt's
      documented contract rather than an observed response.

## Verification steps

1. Connect the dedicated Trakt test account (a free account is enough — the
   100-favorite cap is the same for every tier).
2. Favorite a movie and a series locally; confirm both appear on Trakt, and
   that the connection's count moves.
3. Favorite something on Trakt, run **Sync with Trakt** with Favorites ticked,
   and confirm the preview names it and applying flags it here.
4. Unfavorite on each side in turn; confirm the removal travels the other way
   and is not undone by the next reconciliation.
5. Fill the remote list to 100, then favorite one more locally: the push must
   end terminally with the title named in Settings, and the local favorite must
   stay.
6. Confirm a favorite on a season or episode never reaches Trakt.
