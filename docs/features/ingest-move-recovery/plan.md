# Recoverable Library Moves

Status: On Hold
Created: 2026-09-23
Updated: 2026-09-23

## Goal and owner decision

Recover library organization across filesystem/database interruption without
losing file ownership or overwriting a different version. On 2026-09-23 the owner
chose to treat the National Treasure incident as an unproven one-off and watch for
recurrence, prioritizing Media Server-owned staging cleanup and publication while seeding.
This plan is deliberately parked and authorizes no implementation or monitoring
automation.

## Evidence and limits

The operator reported an unreadable canonical MKV and a playable staging file of
similar size. Manual replacement allowed Probe to succeed. No original corrupt
bytes, hashes or complete operation history remain to establish the cause.
Current organization uses File.Move; these observations do not establish a defect
in File.Move or prove that the torrent engine recreated the source.

## Remaining deliverables if resumed

- [ ] Persist per-file move intent/outcome before filesystem mutation, including
  source/destination and sufficient evidence for recovery after DB-save failure.
- [ ] Recover interrupted moves idempotently; preserve both files and request review
  for ambiguous destinations, including different same-size files. Preserve existing
  alternate-version collision handling and same-filesystem rename behavior.
- [ ] Add focused probe/open and move diagnostics that distinguish missing files,
  access failure and unreadable headers without claiming full-file integrity from
  a successful probe.
- [ ] Add fault-injection coverage around move and DB persistence, verify known-fixture
  hashes on disposable Windows/Docker storage, update current behavior docs and bump
  the runtime version when implementation ships.

## Dependencies and verification

Media Server owns staging; Torrent Engine remains a generic downloader.
Copy-for-seeding placement, engine release ordering and cleanup belong to the
[Media Server seeding plan](../torrents-and-organizer/plan.md). This parked plan
retains only the broader crash-recovery work beyond that feature; deliverables
must not be duplicated when either plan is implemented. Build/test the API and exercise source-only, destination-only,
both-present and neither-present recovery, restart and unavailable volume cases
before completing a resumed implementation. No automatic repair of the operator's
library is authorized.
