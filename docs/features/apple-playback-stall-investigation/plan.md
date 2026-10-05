---
status: On Hold
created: 2026-10-02
updated: 2026-10-02
summary: Find and fix why Apple TV playback occasionally freezes or loses audio, one symptom at a time.
---

# Apple Playback Stalls and Audio Dropouts

## Goal and owner direction

Identify why playback on Apple TV occasionally freezes or loses audio, establish
which component is responsible for each symptom, and verify targeted corrections.
On 2026-10-02 the owner reported
that the buffers work but stalls persist and explicitly deferred this investigation
and further fixes to separate later work. This plan does not authorize starting
the investigation now.

## Reported problems

The owner described two problems on 2026-10-02. These are device observations,
not yet reproduced or attributed to a component by an investigation.

1. **Playback freezes**, with two observed recovery patterns:
   - A severe freeze lasts several seconds, followed by a brief dark screen;
     the picture then returns and playback resumes. The owner perceives this as
     the system detecting the stall and reloading the stream. A stream reload
     or recovery trigger has not yet been confirmed from diagnostics.
   - A shorter hitch clears by itself without the visible dark-screen restart.
     It may share the same cause and recover before a watchdog intervenes, but
     the relationship between these patterns is unconfirmed.
2. **Audio disappears while video continues normally.** Seeking approximately
   ten seconds forward or backward restores sound. After seeking backward, audio
   is present at the same media position that was previously silent. Record both
   the first loss and replay of that interval when reproducing the problem.

The owner suggested MP4 composition/packaging as a possible explanation for the
audio problem. This is a hypothesis only; neither a packaging defect nor a shared
cause with the freezes is established. Working buffers do not rule out client,
player, network, server or storage faults.

## Baseline and target behavior

[Apple playback buffering](../apple-playback-buffering/feature.md) provides disabled,
memory and disk cache modes with common player/network diagnostics and retained
stall/recovery snapshots. Its device experiment is complete; it does not establish
stall-free playback or a root cause.

The target is evidence-backed correction of the observed interruptions. Keep client
loading, AVPlayer behavior, network delivery, server range/remux processing and
storage latency as candidate boundaries until measurements distinguish them.
Do not assume that a larger buffer or a packaging change is the remedy.

## Deliverables

### Phase 1 — reproduce and localize

- [ ] D1. Record a reproducible source/track selection, playback position and observation
  duration, exact client/server/tvOS versions, device, network path and cache mode.
- [ ] D2. Reproduce and distinguish the severe freeze/dark-screen recovery and the
  shorter self-recovering hitch; identify the actual recovery trigger from logs.
- [ ] D3. Reproduce audio loss with uninterrupted video, recording the selected audio
  track, output route, seek direction/distance and sound at the same position after replay.
- [ ] D4. Capture diagnostic snapshots at freezes and recoveries and correlate them
  with timed server requests and storage/network observations. Record any missing
  telemetry needed to distinguish the candidate boundaries.
- [ ] D5. Compare disabled, memory and disk cache modes on the same remux source under
  controlled conditions; account for direct play bypassing the application cache.
- [ ] D6. Establish the failing boundary and supported root-cause hypothesis for each
  problem, including competing explanations and reproducers suitable for validating
  corrections. Determine whether the freeze variants and audio loss share a cause.

### Phase 2 — correct and verify

- [ ] D7. Define the smallest evidence-supported correction and its affected components
  before implementation; coordinate with their existing plans without duplicating work.
- [ ] D8. Implement the correction and appropriate regression coverage, preserving
  source/track selection, seeking, resume and bounded resource use.
- [ ] D9. Repeat the failing device scenario and baseline comparisons; record stalls,
  recoveries, audio continuity and observation duration. Automatic recovery alone
  is not uninterrupted playback; seeking to restore sound is not a fix.
- [ ] D10. Update affected reality documentation, record verification and version outcomes,
  remove this plan after all deliverables are complete, and regenerate the docs index.

## Interactions and open questions

The [Apple client](../apple-client/feature.md) owns player/loader behavior and
diagnostics; [remux streaming](../remux-streaming/feature.md) owns virtual media
delivery. Its [existing plan](../remux-streaming/plan.md) retains its format and
load-test deliverables. This investigation owns the reported intermittent freezes
and audio loss, including their possible relationship.

The failing component, the reproducible media/conditions, necessary telemetry and
the correction are not yet established. Set this plan Ready only after the owner
resumes and explicitly approves a sufficiently defined scope.

## Verification

When resumed, collect real-device evidence first, then run the affected components'
builds and tests and repeat the same controlled playback scenario after correction.
Keep results and uncertainty explicit. Documentation changes require
`node scripts/docs-index.mjs --check` and `git diff --check`.
