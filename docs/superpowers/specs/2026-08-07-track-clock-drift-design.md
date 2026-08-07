# Track post-anchor clock drift in TimedAudioBuffer

Status: approved design, pre-implementation.
Origin: windowsSpin issue #63 (sync drift over multi-hour sessions), investigated 2026-08-07.

## Problem

`TimedAudioBuffer` converts the server schedule to local time exactly once, when playback
anchors (`ScheduledLocalTimeFor` runs only while `!_playbackStarted`). After that the sync
error is pure pace — `elapsed local time - samples read time` — with no server-clock term.
The Kalman synchronizer keeps tracking the true server<->client offset, but nothing applies
it: on a gapless multi-hour stream, relative crystal drift (tens to hundreds of ppm,
~0.1-1 s over a few hours) accumulates as absolute misalignment that no counter, log, or
correction ever sees. Characterized against the real buffer: a 200 ms offset slew leaves
`SyncErrorMicroseconds` at -0.0 ms while true misalignment reaches the full 200 ms.

Two follow-on symptoms confirmed by the same characterization:

- Any re-anchor (`ResetSyncTracking` — device switch, static-delay nudge, pause/unpause)
  silently wipes the accumulated drift, so users who compensate with static delay
  double-correct and oscillate ("I feel like I am going crazy" — issue #63).
- `drops=0 inserts=0` in sync-health logs reads as healthy when it is actually blindness.

Both windowsSpin and sendspin-player consume this code via the `Sendspin.SDK` NuGet
package (9.1.0), so an SDK fix repairs both clients.

## Decisions (agreed 2026-08-07)

| Decision | Choice |
|---|---|
| Release train | 9.x patch first: branch from `v9.1.0` (drift-relevant files are byte-identical to `main`, so forward-merge to the v10 line afterward is trivial). Ship as **9.2.0**. |
| Rollout | **Default-on** with opt-out: `SyncCorrectionOptions.TrackClockDrift = true`. Flag-off is bit-identical current behavior, pinned by test. |
| Scope of tracking | **Kalman offset only** — `StaticDelayMs` is excluded by construction. Static-delay changes keep today's explicit re-anchor semantics (`ReanchorTiming`). |
| Approach | **A: offset-delta term** folded into the existing error (below). Rejected: full position-servo rewrite (same steady-state math, far larger regression surface); periodic audit re-anchor (discrete corrections, new timer machinery, coarser alignment). |

## Mechanism

New state in `TimedAudioBuffer`:

- `_clockOffsetAtAnchorUs` — the Kalman offset (`_clockSync.GetStatus().OffsetMicroseconds`)
  captured when the sync-error reference is (re)established.
- `_clockDriftUs` — the latest computed drift term.

In `CalculateSyncError`:

```
if (TrackClockDrift && _clockSync.IsConverged)
    _clockDriftUs = currentOffset - _clockOffsetAtAnchorUs;   // else: hold last value

_currentSyncErrorMicroseconds = elapsed - samplesReadTime - baseline + (long)_clockDriftUs;
```

Sign convention (schedule moved earlier -> playing late -> positive error -> speed up) is
verified by the red test, not by argument. The exact semantics of
`ClockSyncStatus.OffsetMicroseconds` (server-client vs client-server) are confirmed during
implementation; the red test fails loudly if the sign is wrong.
Post-implementation note: the red test confirmed the shipped order is `currentOffset - _clockOffsetAtAnchorUs` (offset = server - client rises => schedule earlier => positive error), as reflected above.

**Offset recapture rule:** `_clockOffsetAtAnchorUs` is recaptured at exactly the places
that already establish or absorb the error baseline — playback anchor (`_playbackStarted`
transition in `Read`/`ReadRaw`), `CaptureSyncErrorBaseline` (startup and reconnect
variants), `ResetSyncTracking`, and `Clear`. `_clockDriftUs` resets to 0 at the same
points. One rule, no new lifecycle.

**Deferred capture:** if the clock is not converged at a recapture point (possible on the
`ReadRaw` pre-convergence anchor path, or when a stabilization window closes before the
Kalman filter re-converges), the capture is deferred: the drift term stays 0 and the
reference is taken on the first *converged* `CalculateSyncError`. This prevents an
unconverged garbage reference from turning the eventual convergence step into false
drift.

Downstream is untouched. The term flows into the existing ladder — EMA smoothing (alpha
0.1) filters Kalman jitter, the deadband gates action, the resampling band slews
inaudibly, drop/insert covers the mid range, and the >500 ms re-anchor threshold catches
clock insanity. Steady state at 100 ppm real drift: needed rate authority is 100 ppm
against 20,000 ppm available; absolute alignment rides the deadband at ~1-2 ms sustained
(vs ~1 s per 3 h today). The deadband limit cycle this creates is the one windowsSpin
PR #64 made harmless (WDL filter chain no longer toggles at unity).

## Guardrails

- **Unconverged clock** (`!IsConverged`): the term holds its last value — never zeroed
  mid-flight (a zero would inject a step), never fed unconverged garbage.
- **Reconnect:** the existing stabilization window suppresses corrections; the recapture
  at window end refreshes the offset reference, so the re-converged Kalman's new constant
  is absorbed, not corrected. Misalignment accrued during an outage stays absorbed —
  identical to today's post-reconnect semantics (no regression; stream restarts cover
  large gaps).
- **Kalman step > re-anchor threshold:** the term drives the error past 500 ms -> existing
  re-anchor snaps cleanly; the existing cooldown prevents thrash; `Clear` recaptures.
- **Flag off:** term never computed; behavior bit-identical to 9.1.0.

## API surface (all additive, 9.2.0)

- `SyncCorrectionOptions.TrackClockDrift { get; set; } = true` (+ `Clone()` entry).
- `AudioBufferStats.ClockDriftMs` — current drift term in ms, via `GetStats()`. Makes the
  behavior observable (stats-for-nerds, sync-health logging) and gives windowsSpin's
  `EpisodeClassifier` the input it needs to diagnose device-clock skew in Combined mode
  (that classifier wiring is windowsSpin follow-up, not this change).
- No interface changes; `ITimedAudioBuffer` untouched. Both `Read` (internal corrector)
  and `ReadRaw` (external corrector — windowsSpin, sendspin-player) inherit the behavior
  because it lives in `CalculateSyncError`.

## Testing

New `TimedAudioBufferClockDriftTests` with a fake `IClockSynchronizer` (settable offset,
convergence, static delay — ported from the investigation's characterization harness):

1. **Drift tracked (red first):** slew offset -200 ms during playback; sync error reflects
   it; on the internal `Read` path consumption catches up and true misalignment (write
   head minus buffered depth vs `ServerToClientTime`) stays bounded instead of reaching
   200 ms.
2. **Opt-out pins 9.1.0 behavior:** same slew, `TrackClockDrift = false` -> error ~0.
3. **Static delay excluded:** `StaticDelayMs` change mid-playback -> no drift contribution.
4. **Unconverged freeze:** `IsConverged = false` -> term holds; no injection.
5. **Reconnect absorption:** offset step inside the stabilization window -> no error spike
   after the window's recapture.
6. **Deferred capture:** anchor while `IsConverged = false`, converge later with a
   different offset -> the convergence step is not reported as drift; the reference is
   taken at first converged calculation.

Full existing SDK suite stays green. Smoke: build windowsSpin against the local SDK
(`-p:UseSdkSource=true`) before publishing.

## Rollout

1. Implement on `feat/track-clock-drift` (this worktree, branched from `v9.1.0`).
2. Ship `Sendspin.SDK 9.2.0`.
3. windowsSpin: one-line package bump PR -> dev build for issue #63 field verification
   (watch `ClockDriftMs` accumulate; delay-nudge snap-back test should become a no-op).
4. sendspin-player: package bump on its own schedule.
5. Forward-merge to the v10 line on `main` (drift-relevant files identical; expected
   clean).
