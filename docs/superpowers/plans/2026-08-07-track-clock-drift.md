# Track Clock Drift Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `TimedAudioBuffer` track post-anchor Kalman clock-offset movement so absolute playback alignment holds over multi-hour gapless streams (windowsSpin issue #63, drift half).

**Architecture:** One additive drift term (`current Kalman offset − offset at anchor`) folded into the existing sync-error calculation, with the offset reference recaptured at every point that already resets or absorbs the error baseline. Guardrails: term frozen while unconverged, capture deferred if unconverged at a recapture point. Surfaced via a new `AudioBufferStats.ClockDriftMs`. Spec: `docs/superpowers/specs/2026-08-07-track-clock-drift-design.md`.

**Tech Stack:** C# (.NET 8/10 multi-target), xUnit, worktree `C:\CodeProjects\SendspinSDK-clock-drift`, branch `feat/track-clock-drift` (from `v9.1.0`).

## Global Constraints

- All commands run from `C:\CodeProjects\SendspinSDK-clock-drift`.
- Test command: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q` (append `--filter "FullyQualifiedName~<Name>"` for a single test).
- The full suite must be green at every commit. (Baseline at `v9.1.0` is green.)
- Commit messages: repo conventional style (`test:`, `feat:`, `fix:`, `chore:`). **Never add AI attribution, `Co-Authored-By`, or any self-reference.**
- New/modified code must introduce no new build warnings in touched files. Match surrounding style (file-scoped namespaces, `var`, XML doc comments on public members). New test files follow `TimedAudioBufferTimingTests.cs` conventions (no copyright header).
- Sign convention (fixed by `KalmanClockSynchronizer`): `ClientToServerTime(c) = c + offset`, `ServerToClientTime(s) = s − offset − staticDelayUs`. I.e. `offset = server − client`; positive offset means the server clock is ahead. `GetStatus().OffsetMicroseconds` returns this offset **without** static delay.
- Drift term sign (derived, verified by Task 3's red test): scheduled client time for server position `P` is `P − offset`. If the offset **rises**, the schedule moves **earlier**, we are playing **late**, and the error must be **positive** (positive error ⇒ drop/speed-up). Therefore `driftUs = currentOffset − offsetAtAnchor`.

---

### Task 1: Align FakeClockSynchronizer with the real Kalman sign convention

The existing test fake uses `client = server + offset` — the **opposite** of the real
`KalmanClockSynchronizer` (`client = server − offset`). Any drift test written against the
current fake would pass with an inverted term and fail against production. Flip the fake to
the real convention and mechanically negate the offsets in the one test file that uses it.

**Files:**
- Create: `tests/Sendspin.SDK.Tests/Audio/FakeClockSynchronizerTests.cs`
- Modify: `tests/Sendspin.SDK.Tests/Audio/FakeClockSynchronizer.cs`
- Modify: `tests/Sendspin.SDK.Tests/Audio/TimedAudioBufferTimingTests.cs` (7 offset assignments)

**Interfaces:**
- Consumes: `IClockSynchronizer`, `ClockSyncStatus` (existing).
- Produces: `FakeClockSynchronizer` with Kalman-convention `OffsetMicroseconds` (`server − client`), used by every later task's tests.

- [ ] **Step 1: Write the convention-pinning test (failing)**

Create `tests/Sendspin.SDK.Tests/Audio/FakeClockSynchronizerTests.cs`:

```csharp
namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Pins the fake to the real KalmanClockSynchronizer sign convention:
/// ClientToServerTime(c) = c + offset, ServerToClientTime(s) = s - offset - staticDelayUs,
/// GetStatus().OffsetMicroseconds = offset (no static delay). A fake with an inverted
/// convention makes drift tests pass with the wrong sign against production.
/// </summary>
public class FakeClockSynchronizerTests
{
    [Fact]
    public void Conversions_MatchKalmanConvention()
    {
        var fake = new FakeClockSynchronizer
        {
            OffsetMicroseconds = 5_000_000,
            StaticDelayMs = 100,
        };

        Assert.Equal(1_000_000 + 5_000_000, fake.ClientToServerTime(1_000_000));
        Assert.Equal(9_000_000 - 5_000_000 - 100_000, fake.ServerToClientTime(9_000_000));
        Assert.Equal(5_000_000, fake.GetStatus().OffsetMicroseconds);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q --filter "FullyQualifiedName~FakeClockSynchronizerTests"`
Expected: FAIL — the current fake computes `ServerToClientTime = s + offset - delay`, so the second assertion gets `13_900_000`, not `3_900_000`.

- [ ] **Step 3: Flip the fake's convention**

In `tests/Sendspin.SDK.Tests/Audio/FakeClockSynchronizer.cs`, replace the offset doc comment and the two conversion methods:

```csharp
    /// <summary>
    /// Offset applied in conversions, matching KalmanClockSynchronizer's convention:
    /// offset = server_time - client_time, so client_time = server_time - offset.
    /// Zero mimics the pre-sync state (raw server timestamps pass through).
    /// </summary>
    public long OffsetMicroseconds { get; set; }
```

```csharp
    public long ServerToClientTime(long serverTime) =>
        serverTime - OffsetMicroseconds - (long)(StaticDelayMs * 1000);

    public long ClientToServerTime(long clientTime) =>
        clientTime + OffsetMicroseconds - (long)(StaticDelayMs * 1000);
```

(Note the static-delay term in `ClientToServerTime` mirrors the fake's previous symmetry; the real Kalman applies static delay only in `ServerToClientTime`, and no current test exercises `ClientToServerTime` with a static delay. Keep `GetStatus()` as-is — it already returns `OffsetMicroseconds`, which is now Kalman-consistent.)

- [ ] **Step 4: Negate the 7 offset assignments in TimedAudioBufferTimingTests.cs**

Each assignment expressed "client-ahead" offsets; under the Kalman convention they negate. Exact replacements (locate by the old text; line numbers approximate):

| Old (client = server + offset) | New (offset = server − client) |
|---|---|
| `clockSync.OffsetMicroseconds = LocalNow - 400_000 - ServerT0;` (~L60) | `clockSync.OffsetMicroseconds = ServerT0 - (LocalNow - 400_000);` |
| `clockSync.OffsetMicroseconds = LocalNow + 100_000 - ServerT0;` (~L85) | `clockSync.OffsetMicroseconds = ServerT0 - (LocalNow + 100_000);` |
| `clockSync.OffsetMicroseconds = LocalNow - ServerT0;` (~L110) | `clockSync.OffsetMicroseconds = ServerT0 - LocalNow;` |
| `clockSync.OffsetMicroseconds = LocalNow - ServerT0;` (~L144) | `clockSync.OffsetMicroseconds = ServerT0 - LocalNow;` |
| `clockSync.OffsetMicroseconds = LocalNow - ServerT0;` (~L207) | `clockSync.OffsetMicroseconds = ServerT0 - LocalNow;` |
| `OffsetMicroseconds = LocalNow + 100_000 - ServerT0,` (~L286, object initializer) | `OffsetMicroseconds = ServerT0 - (LocalNow + 100_000),` |

After editing, verify no stragglers: `grep -n "OffsetMicroseconds = Local" tests/Sendspin.SDK.Tests/Audio/TimedAudioBufferTimingTests.cs` must return nothing. (If grep finds a 7th assignment not listed here, apply the same negation rule.)

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q`
Expected: PASS, 0 failures (the timing tests are convention-symmetric once negated; the new pin test passes).

- [ ] **Step 6: Commit**

```bash
git add tests/Sendspin.SDK.Tests/Audio/
git commit -m "test: align FakeClockSynchronizer with the Kalman sign convention"
```

---

### Task 2: Add SyncCorrectionOptions.TrackClockDrift

**Files:**
- Modify: `src/Sendspin.SDK/Audio/SyncCorrectionOptions.cs`
- Modify: `tests/Sendspin.SDK.Tests/Audio/SyncCorrectionOptionsTests.cs`

**Interfaces:**
- Produces: `SyncCorrectionOptions.TrackClockDrift` (bool, default `true`), copied by `Clone()`. Consumed by Task 3.

- [ ] **Step 1: Write the failing tests**

Append to the existing test class in `SyncCorrectionOptionsTests.cs`:

```csharp
    [Fact]
    public void TrackClockDrift_DefaultsToTrue()
    {
        Assert.True(SyncCorrectionOptions.Default.TrackClockDrift);
    }

    [Fact]
    public void Clone_CopiesTrackClockDrift()
    {
        var options = new SyncCorrectionOptions { TrackClockDrift = false };
        Assert.False(options.Clone().TrackClockDrift);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q --filter "FullyQualifiedName~SyncCorrectionOptionsTests"`
Expected: FAIL to compile — `TrackClockDrift` does not exist. (A compile error in the test project is the red state here.)

- [ ] **Step 3: Implement**

In `SyncCorrectionOptions.cs`, add after the `ReconnectStabilizationMicroseconds` property:

```csharp
    /// <summary>
    /// When true (default), the sync error tracks post-anchor movement of the Kalman
    /// clock offset, so absolute alignment to the server schedule holds over long
    /// gapless streams instead of drifting with relative crystal error. Static delay
    /// is excluded; delay changes keep their explicit re-anchor semantics.
    /// Set false to restore the pre-9.2 pace-only behavior.
    /// </summary>
    public bool TrackClockDrift { get; set; } = true;
```

And add to `Clone()`'s initializer list:

```csharp
        TrackClockDrift = TrackClockDrift,
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q --filter "FullyQualifiedName~SyncCorrectionOptionsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Audio/SyncCorrectionOptions.cs tests/Sendspin.SDK.Tests/Audio/SyncCorrectionOptionsTests.cs
git commit -m "feat: add SyncCorrectionOptions.TrackClockDrift (default on)"
```

---

### Task 3: Core drift term in CalculateSyncError

The minimal version: capture the offset unconditionally at the recapture points, update the
term unconditionally per calculation. (Convergence gating and deferred capture are Task 4 —
they get their own red tests.)

**Files:**
- Create: `tests/Sendspin.SDK.Tests/Audio/TimedAudioBufferClockDriftTests.cs`
- Modify: `src/Sendspin.SDK/Audio/TimedAudioBuffer.cs`

**Interfaces:**
- Consumes: `FakeClockSynchronizer` (Task 1 convention), `SyncCorrectionOptions.TrackClockDrift` (Task 2).
- Produces: fields `_clockOffsetAtAnchorUs` (double), `_clockDriftUs` (double), `_clockOffsetCaptured` (bool); private method `CaptureClockOffsetReference()`; drift-aware `CalculateSyncError`. Task 4 hardens `CaptureClockOffsetReference` and the update gate; Task 6 reads `_clockDriftUs` for stats.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Audio/TimedAudioBufferClockDriftTests.cs`:

```csharp
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Issue #63 drift half: the sync error must track post-anchor movement of the Kalman
/// clock offset (TrackClockDrift, default on). Without it, relative crystal drift
/// accumulates as absolute misalignment that no counter or correction ever sees.
/// Offset convention (Task 1): offset = server - client; scheduled client time for
/// server position P is P - offset, so a RISING offset moves the schedule earlier,
/// the player is LATE, and the sync error must go POSITIVE (positive = drop/speed up).
/// </summary>
public class TimedAudioBufferClockDriftTests
{
    private const int SamplesPerMs = 96; // 48kHz stereo interleaved
    private const int StepMs = 10;
    private const int StepSamples = StepMs * SamplesPerMs;
    private const long ServerT0 = 1_000_000;
    private const long LocalT0 = 9_000_000_000_000;

    private static readonly AudioFormat Format = new()
    {
        Codec = "pcm",
        SampleRate = 48_000,
        Channels = 2,
    };

    private sealed class Session : IDisposable
    {
        public FakeClockSynchronizer ClockSync { get; } = new();

        public TimedAudioBuffer Buffer { get; }

        public long WallNow { get; private set; } = LocalT0;

        public long WriteServerTs { get; private set; } = ServerT0;

        private readonly float[] _chunk = new float[StepSamples];
        private readonly float[] _readBuf = new float[StepSamples];
        private readonly bool _useRawReads;

        public Session(SyncCorrectionOptions? options, bool useRawReads)
        {
            _useRawReads = useRawReads;
            Buffer = new TimedAudioBuffer(Format, ClockSync, bufferCapacityMs: 5000, options);
            Array.Fill(_chunk, 0.25f);

            // Converged clock scheduling the first chunk right now.
            ClockSync.OffsetMicroseconds = ServerT0 - LocalT0;
            ClockSync.IsConverged = true;
            ClockSync.HasMinimalSync = true;

            // ~2s producer pre-roll (server transmit-ahead).
            for (var i = 0; i < 200; i++)
            {
                WriteChunk();
            }
        }

        public void WriteChunk()
        {
            Buffer.Write(_chunk, WriteServerTs);
            WriteServerTs += StepMs * 1000L;
        }

        /// <summary>Advances wall time one 10ms step, keeps the producer ahead, reads one step.</summary>
        public void Step()
        {
            WallNow += StepMs * 1000L;
            WriteChunk();
            if (_useRawReads)
            {
                Buffer.ReadRaw(_readBuf, WallNow);
            }
            else
            {
                Buffer.Read(_readBuf, WallNow);
            }
        }

        public void Steps(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Step();
            }
        }

        /// <summary>
        /// True misalignment of the read cursor vs the schedule, in microseconds
        /// (positive = playing late). Cursor server position = write head minus
        /// buffered depth; it SHOULD play at ServerToClientTime(position).
        /// </summary>
        public long TrueMisalignmentUs()
        {
            var cursorServerPos = WriteServerTs - (long)(Buffer.BufferedMilliseconds * 1000);
            return WallNow - ClockSync.ServerToClientTime(cursorServerPos);
        }

        /// <summary>Slews the Kalman offset by <paramref name="totalUs"/> evenly across steps.</summary>
        public void SlewOffset(long totalUs, int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                ClockSync.OffsetMicroseconds += totalUs / steps;
                Step();
            }
        }

        public void Dispose() => Buffer.Dispose();
    }

    [Fact]
    public void OffsetSlew_SurfacesInSyncError_OnRawPath()
    {
        // ReadRaw applies no corrections itself (external correctors do), so the
        // error must accumulate to roughly the slewed amount.
        using var session = new Session(options: null, useRawReads: true);
        session.Steps(300); // 3s: anchor + startup grace + baseline capture settle

        session.SlewOffset(totalUs: 200_000, steps: 600); // +200ms over 6s

        Assert.InRange(session.Buffer.SmoothedSyncErrorMicroseconds, 150_000, 250_000);
    }

    [Fact]
    public void OffsetSlew_InternalReadPath_CatchesUp()
    {
        // Internal Read applies drop/insert above ResamplingThreshold; with a tight
        // threshold the loop closes and TRUE misalignment stays bounded near the
        // threshold instead of reaching the slewed 200ms.
        var options = new SyncCorrectionOptions { ResamplingThresholdMicroseconds = 5_000 };
        using var session = new Session(options, useRawReads: false);
        session.Steps(300);

        session.SlewOffset(totalUs: 200_000, steps: 600);
        session.Steps(600); // 6s settle after slew ends

        var stats = session.Buffer.GetStats();
        Assert.True(stats.SamplesDroppedForSync > 0, "drift should trigger catch-up drops");
        Assert.InRange(session.TrueMisalignmentUs(), -20_000, 20_000);
    }

    [Fact]
    public void OffsetSlew_FlagOff_PinsPreDriftBehavior()
    {
        var options = new SyncCorrectionOptions { TrackClockDrift = false };
        using var session = new Session(options, useRawReads: true);
        session.Steps(300);

        session.SlewOffset(totalUs: 200_000, steps: 600);

        // Pre-9.2 behavior: the pace servo is blind to the slew...
        Assert.InRange(session.Buffer.SmoothedSyncErrorMicroseconds, -5_000, 5_000);
        // ...while true misalignment reaches the full slew.
        Assert.InRange(session.TrueMisalignmentUs(), 150_000, 250_000);
    }
}
```

- [ ] **Step 2: Run to verify the right failures**

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q --filter "FullyQualifiedName~TimedAudioBufferClockDriftTests"`
Expected: `OffsetSlew_SurfacesInSyncError_OnRawPath` FAILS (smoothed error ~0, needs ≥150,000) and `OffsetSlew_InternalReadPath_CatchesUp` FAILS (`TrueMisalignmentUs()` ~200,000, no drops). `OffsetSlew_FlagOff_PinsPreDriftBehavior` PASSES (it pins today's behavior). If the flag-off test fails instead, the harness is wrong — stop and fix the test, not the code.

- [ ] **Step 3: Implement the minimal drift term**

All edits in `src/Sendspin.SDK/Audio/TimedAudioBuffer.cs`.

**3a — fields.** Next to the existing baseline fields (`_syncErrorBaselineMicroseconds` / `_syncErrorBaselineCaptured`, around line 112), add:

```csharp
    // Post-anchor clock-drift tracking (SyncCorrectionOptions.TrackClockDrift):
    // the Kalman offset captured when the sync-error reference was (re)established,
    // and the latest drift term (current offset - anchor offset). A rising offset
    // moves the schedule earlier => playing late => positive error contribution.
    private double _clockOffsetAtAnchorUs;
    private double _clockDriftUs;
    private bool _clockOffsetCaptured;
```

**3b — capture helper.** Add next to `CaptureSyncErrorBaseline` (after its closing brace):

```csharp
    /// <summary>
    /// (Re)captures the Kalman offset used as the drift reference and zeroes the
    /// drift term. Called wherever the sync-error baseline is established or
    /// absorbed, so constant offsets rebase while later movement counts as drift.
    /// Must be called under lock.
    /// </summary>
    private void CaptureClockOffsetReference()
    {
        _clockOffsetAtAnchorUs = _clockSync.GetStatus().OffsetMicroseconds;
        _clockOffsetCaptured = true;
        _clockDriftUs = 0;
    }
```

**3c — call sites.** Add `CaptureClockOffsetReference();` at each of these five points:

1. `Read` playback anchor — immediately after `_playbackStartLocalTime = currentLocalTime - CalibratedStartupLatencyMicroseconds;` and the two counter resets in the `!_playbackStarted` block (~line 391).
2. `ReadRaw` playback anchor — same pattern (~line 543).
3. `CaptureSyncErrorBaseline` — this one is NOT a plain append. The smoothed error at a
   baseline capture already **contains** the drift term; if the baseline absorbs it and the
   re-reference zeroes it, the same microseconds are absorbed twice and reappear as an
   equal-and-opposite error on the next calculation. Remove the drift contribution first,
   then absorb only the pace residue. Replace the method body up to (not including) the
   `if (Math.Abs(delta) >= 1_000)` logging block with:

```csharp
    private void CaptureSyncErrorBaseline(string reason)
    {
        // Remove the drift contribution before snapshotting: post-anchor clock
        // movement is handled by re-referencing the offset (below), not by folding
        // it into the pace baseline - otherwise the same microseconds would be
        // absorbed twice and reappear as an equal-and-opposite error once the
        // drift term resets to zero.
        _smoothedSyncErrorMicroseconds -= _clockDriftUs;
        _currentSyncErrorMicroseconds -= (long)_clockDriftUs;
        CaptureClockOffsetReference();

        var delta = _smoothedSyncErrorMicroseconds;
        _syncErrorBaselineMicroseconds += delta;
        _smoothedSyncErrorMicroseconds = 0;
        _currentSyncErrorMicroseconds -= (long)delta;
        _syncErrorBaselineCaptured = true;
```

   (The existing logging block stays exactly as-is. At the startup capture the drift term
   is ~0, so this reduces to the old behavior; at reconnect captures it prevents the
   double absorption.)
4. `ResetSyncTracking` — with the other sync-error field resets, replace nothing; add after `_syncErrorBaselineCaptured = false;`:

```csharp
            _clockOffsetCaptured = false;
            _clockDriftUs = 0;
```

(Reset defers to the next anchor's capture rather than capturing here — `ResetSyncTracking` intentionally leaves `_playbackStarted = false`, so the next read re-anchors and captures.)

5. `Clear` — same two lines after its `_syncErrorBaselineCaptured = false;`.

**3d — the term.** In `CalculateSyncError`, replace:

```csharp
        _currentSyncErrorMicroseconds = elapsedTimeMicroseconds - samplesReadTimeMicroseconds
            - (long)_syncErrorBaselineMicroseconds;
```

with:

```csharp
        // Post-anchor server-clock movement. The pace terms above hold consumption
        // to the LOCAL clock; without this term the Kalman offset's movement since
        // anchor (relative crystal drift) accumulates as invisible absolute
        // misalignment (issue #63). Sign: offset = server - client and scheduled
        // client time is (server - offset), so a rising offset means the schedule
        // moved earlier and we are late => positive contribution.
        if (_syncOptions.TrackClockDrift && _clockOffsetCaptured)
        {
            _clockDriftUs = _clockSync.GetStatus().OffsetMicroseconds - _clockOffsetAtAnchorUs;
        }

        _currentSyncErrorMicroseconds = elapsedTimeMicroseconds - samplesReadTimeMicroseconds
            - (long)_syncErrorBaselineMicroseconds + (long)_clockDriftUs;
```

(Lock-ordering note: `CalculateSyncError` and `CaptureClockOffsetReference` run under the buffer lock and call `_clockSync.GetStatus()`, which takes the Kalman lock. That ordering — buffer lock → Kalman lock — already exists via `ScheduledLocalTimeFor`'s `ServerToClientTime` call, and the synchronizer never calls into the buffer, so no inversion is possible.)

- [ ] **Step 4: Run the new tests, then the full suite**

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q --filter "FullyQualifiedName~TimedAudioBufferClockDriftTests"`
Expected: all 3 PASS.

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q`
Expected: PASS, 0 failures. (The timing tests keep converged clocks with constant offsets, so the new term is 0 there; any failure means a capture site was missed or double-applied — investigate before proceeding.)

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Audio/TimedAudioBuffer.cs tests/Sendspin.SDK.Tests/Audio/TimedAudioBufferClockDriftTests.cs
git commit -m "feat: track post-anchor clock drift in the sync error"
```

---

### Task 4: Guardrails — unconverged freeze and deferred capture

Task 3's minimal version updates the term unconditionally and captures whatever
`GetStatus()` returns. Two red tests force the spec's guardrails: hold the term while the
clock is unconverged, and defer the reference capture if the clock was unconverged at a
recapture point.

**Files:**
- Modify: `tests/Sendspin.SDK.Tests/Audio/TimedAudioBufferClockDriftTests.cs`
- Modify: `src/Sendspin.SDK/Audio/TimedAudioBuffer.cs`

**Interfaces:**
- Consumes: Task 3's fields/methods.
- Produces: converged-gated `CaptureClockOffsetReference()` and drift update; consumed as-is by Tasks 5-6.

- [ ] **Step 1: Write the failing tests**

Append to `TimedAudioBufferClockDriftTests`:

```csharp
    [Fact]
    public void UnconvergedClock_DriftTermFrozen()
    {
        using var session = new Session(options: null, useRawReads: true);
        session.Steps(300);

        session.SlewOffset(totalUs: 50_000, steps: 200); // +50ms while converged

        // Convergence lost; whatever the status now reports must not move the term.
        session.ClockSync.IsConverged = false;
        session.SlewOffset(totalUs: 100_000, steps: 200);

        Assert.InRange(session.Buffer.SmoothedSyncErrorMicroseconds, 30_000, 70_000);
    }

    [Fact]
    public void UnconvergedAnchor_ConvergenceStepIsNotDrift()
    {
        using var session = new Session(options: null, useRawReads: true);

        // Rewind to an unconverged clock BEFORE the anchor: the Session constructor
        // converged it, so un-converge and shift the reported offset; the anchor
        // must NOT capture this unconverged value as the drift reference.
        session.ClockSync.IsConverged = false;
        session.ClockSync.OffsetMicroseconds += 300_000;

        session.Steps(50); // anchors while unconverged

        // Convergence arrives, settling 300ms away from the unconverged reading.
        session.ClockSync.OffsetMicroseconds -= 300_000;
        session.ClockSync.IsConverged = true;
        session.Steps(300);

        // The convergence step must be absorbed as the reference, not reported as
        // ~300ms of drift.
        Assert.InRange(session.Buffer.SmoothedSyncErrorMicroseconds, -10_000, 10_000);
    }
```

- [ ] **Step 2: Run to verify both fail**

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q --filter "FullyQualifiedName~TimedAudioBufferClockDriftTests"`
Expected: `UnconvergedClock_DriftTermFrozen` FAILS (term follows the unconverged slew to ~150,000) and `UnconvergedAnchor_ConvergenceStepIsNotDrift` FAILS (error ~-300,000 — with the unconverged capture the later convergence step reads as drift; note `SkipStaleAudio`/scheduled-start interactions may shift the exact value, but it is far outside ±10,000). Earlier tests still PASS.

- [ ] **Step 3: Implement the guardrails**

In `TimedAudioBuffer.cs`:

**3a — deferred capture.** Replace the body of `CaptureClockOffsetReference()`:

```csharp
    private void CaptureClockOffsetReference()
    {
        if (_clockSync.IsConverged)
        {
            _clockOffsetAtAnchorUs = _clockSync.GetStatus().OffsetMicroseconds;
            _clockOffsetCaptured = true;
        }
        else
        {
            // Unconverged at a recapture point: defer to the first converged
            // CalculateSyncError so the convergence step becomes the reference,
            // not reported drift.
            _clockOffsetCaptured = false;
        }

        _clockDriftUs = 0;
    }
```

**3b — gated update + deferred completion.** In `CalculateSyncError`, replace the Task 3 drift block:

```csharp
        if (_syncOptions.TrackClockDrift && _clockOffsetCaptured)
        {
            _clockDriftUs = _clockSync.GetStatus().OffsetMicroseconds - _clockOffsetAtAnchorUs;
        }
```

with:

```csharp
        if (_syncOptions.TrackClockDrift)
        {
            if (!_clockOffsetCaptured)
            {
                // Deferred capture: the clock was unconverged at the last recapture
                // point; take the reference from the first converged calculation.
                CaptureClockOffsetReference();
            }
            else if (_clockSync.IsConverged)
            {
                _clockDriftUs = _clockSync.GetStatus().OffsetMicroseconds - _clockOffsetAtAnchorUs;
            }

            // Unconverged with a valid reference: hold the last term (never zero it
            // mid-flight, never follow unconverged readings).
        }
```

- [ ] **Step 4: Run the class, then the full suite**

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q --filter "FullyQualifiedName~TimedAudioBufferClockDriftTests"`
Expected: all 5 PASS.

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q`
Expected: PASS, 0 failures.

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Audio/TimedAudioBuffer.cs tests/Sendspin.SDK.Tests/Audio/TimedAudioBufferClockDriftTests.cs
git commit -m "feat: gate drift tracking on clock convergence with deferred capture"
```

---

### Task 5: Pin static-delay exclusion and reconnect absorption

Both behaviors should already hold from Tasks 3-4 by construction (`GetStatus()` excludes
static delay; the reconnect baseline capture calls `CaptureClockOffsetReference`). These
tests pin them. If either fails, the construction assumption is wrong — treat it as a
Task 3 bug and fix there, not by weakening the test.

**Files:**
- Modify: `tests/Sendspin.SDK.Tests/Audio/TimedAudioBufferClockDriftTests.cs`

**Interfaces:**
- Consumes: Task 3-4 behavior; `TimedAudioBuffer.NotifyReconnect()` (existing).

- [ ] **Step 1: Write the pinning tests**

Append to `TimedAudioBufferClockDriftTests`:

```csharp
    [Fact]
    public void StaticDelayChange_DoesNotEnterDriftTerm()
    {
        using var session = new Session(options: null, useRawReads: true);
        session.Steps(300);

        // A 150ms static-delay change re-schedules via explicit re-anchor in real
        // clients; the drift term must not react to it (Kalman offset unchanged).
        session.ClockSync.StaticDelayMs = 150;
        session.Steps(200);

        Assert.InRange(session.Buffer.SmoothedSyncErrorMicroseconds, -5_000, 5_000);
    }

    [Fact]
    public void ReconnectStabilization_OffsetStepAbsorbedNotCorrected()
    {
        using var session = new Session(options: null, useRawReads: true);
        session.Steps(300);

        // Reconnect: Kalman resets and re-converges 80ms away inside the
        // stabilization window. The window-end recapture must absorb the step.
        session.Buffer.NotifyReconnect();
        session.ClockSync.OffsetMicroseconds += 80_000;
        session.Steps(250); // > 2s stabilization window at 10ms steps

        Assert.InRange(session.Buffer.SmoothedSyncErrorMicroseconds, -10_000, 10_000);
    }
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q --filter "FullyQualifiedName~TimedAudioBufferClockDriftTests"`
Expected: all 7 PASS. These are pins of by-construction behavior, so passing immediately is the expected outcome. If `ReconnectStabilization_OffsetStepAbsorbedNotCorrected` fails with a NEGATIVE settled error (~-80ms), the drift-removal ordering in `CaptureSyncErrorBaseline` (Task 3, call site 3) was not applied — the step got double-absorbed; if it fails with a POSITIVE error (~+80ms), the recapture call is missing entirely. Fix in Task 3's code, not the test. If `StaticDelayChange_DoesNotEnterDriftTerm` fails, the term is reading a delay-inclusive conversion instead of `GetStatus()`.

- [ ] **Step 3: Run the full suite, then commit**

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q`
Expected: PASS, 0 failures.

```bash
git add tests/Sendspin.SDK.Tests/Audio/TimedAudioBufferClockDriftTests.cs
git commit -m "test: pin static-delay exclusion and reconnect absorption for drift tracking"
```

---

### Task 6: Surface ClockDriftMs in AudioBufferStats

**Files:**
- Modify: `src/Sendspin.SDK/Audio/ITimedAudioBuffer.cs` (the `AudioBufferStats` record, ~line 256)
- Modify: `src/Sendspin.SDK/Audio/TimedAudioBuffer.cs` (`GetStats()`)
- Modify: `tests/Sendspin.SDK.Tests/Audio/TimedAudioBufferClockDriftTests.cs`

**Interfaces:**
- Produces: `AudioBufferStats.ClockDriftMs` (double, init-only) — consumed by client diagnostics (windowsSpin stats-for-nerds, sync-health logging; wiring there is follow-up work outside this plan).

- [ ] **Step 1: Write the failing test**

Append to `TimedAudioBufferClockDriftTests`:

```csharp
    [Fact]
    public void GetStats_ReportsClockDriftMs()
    {
        using var session = new Session(options: null, useRawReads: true);
        session.Steps(300);

        session.SlewOffset(totalUs: 200_000, steps: 600);

        Assert.InRange(session.Buffer.GetStats().ClockDriftMs, 150, 250);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q --filter "FullyQualifiedName~GetStats_ReportsClockDriftMs"`
Expected: FAIL to compile — `ClockDriftMs` does not exist.

- [ ] **Step 3: Implement**

In the `AudioBufferStats` record in `ITimedAudioBuffer.cs`, add alongside the other timing properties:

```csharp
    /// <summary>
    /// Gets the post-anchor clock-drift term currently applied to the sync error,
    /// in milliseconds (see <see cref="SyncCorrectionOptions.TrackClockDrift"/>).
    /// Non-zero values show the server<->client clock relationship moving since the
    /// playback anchor; sustained growth indicates relative crystal drift being
    /// actively compensated. Always 0 when drift tracking is disabled.
    /// </summary>
    public double ClockDriftMs { get; init; }
```

In `TimedAudioBuffer.GetStats()`, add to the returned initializer:

```csharp
                ClockDriftMs = _clockDriftUs / 1000.0,
```

- [ ] **Step 4: Run the class, then the full suite**

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q`
Expected: PASS, 0 failures (8 clock-drift tests).

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Audio/ITimedAudioBuffer.cs src/Sendspin.SDK/Audio/TimedAudioBuffer.cs tests/Sendspin.SDK.Tests/Audio/TimedAudioBufferClockDriftTests.cs
git commit -m "feat: expose ClockDriftMs in AudioBufferStats"
```

---

### Task 7: Version 9.2.0, release notes, final verification

**Files:**
- Modify: `src/Sendspin.SDK/Sendspin.SDK.csproj` (`<Version>` ~line 11; `PackageReleaseNotes` block)

- [ ] **Step 1: Bump version and prepend release notes**

In `src/Sendspin.SDK/Sendspin.SDK.csproj`, change:

```xml
    <Version>9.1.0</Version>
```

to:

```xml
    <Version>9.2.0</Version>
```

Locate the `PackageReleaseNotes` property (existing entries are newest-first, formatted like `v9.1.0 - ...`) and prepend, matching the surrounding entry style exactly:

```text
v9.2.0 - Clock Drift Tracking:
- Sync error now tracks post-anchor movement of the Kalman clock offset, holding
  absolute alignment over long gapless streams (windowsSpin issue #63 drift fix)
- New SyncCorrectionOptions.TrackClockDrift (default true); false restores 9.1 behavior
- Drift term is frozen while clock sync is unconverged; reference capture defers to
  first convergence; reconnect offset steps are absorbed, not corrected
- Static delay remains excluded: delay changes keep explicit re-anchor semantics
- New AudioBufferStats.ClockDriftMs surfaces the live drift term for diagnostics
```

- [ ] **Step 2: Full suite + Release build**

Run: `dotnet test tests/Sendspin.SDK.Tests -c Release --nologo -v q`
Expected: PASS, 0 failures.

Run: `dotnet build -c Release --nologo`
Expected: 0 errors; no new warnings from files touched by this plan.

- [ ] **Step 3: windowsSpin source-reference smoke build**

Run (build only — windowsSpin's test project intentionally pins the NuGet SDK):

```bash
dotnet build /c/CodeProjects/windowsSpin-resampler-click/Sendspin.Windows.sln -c Release --nologo -p:UseSdkSource=true "-p:SdkSourcePath=C:\CodeProjects\SendspinSDK-clock-drift"
```

Expected: 0 errors — proves the 9.2.0 surface is drop-in for the flagship consumer. (Uses the click-fix worktree checkout; any master checkout works.)

- [ ] **Step 4: Commit**

```bash
git add src/Sendspin.SDK/Sendspin.SDK.csproj
git commit -m "chore: bump to 9.2.0 with clock drift tracking release notes"
```
