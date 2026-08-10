# v10.0.0 handoff — state as of 2026-08-10

Written to survive a context clear. Everything below was verified against the repo, not
recalled. **Verify again before planning against it** — the previous edition of this file had
three stale "this is blocked" premises within a single session, and this edition already
corrects one of its own (see #89 item 4).

## Repo state

- `main` = `710d782`, carries everything. **Zero open PRs.**
- Version: `<Version>10.0.0</Version>`. Nothing published — CI publishes only on a `v*.*.*`
  tag push (`build.yml`, publish job gated `if: startsWith(github.ref, 'refs/tags/v')`), and
  the tag name is authoritative, overriding the csproj via `-p:Version=`.
- Suite: **626 tests** green on `net10.0`. Release clean on `net8.0` + `net10.0`.
- **Zero IL2026/IL3050.** The CI `aot` job is now a matrix — `linux-x64` **and `linux-arm64`**
  — publishing `tools/AotSmokeTest` with `PublishAot` and running the binary on every push.
- Interop runs live against `aiosendspin[server]==9.0.0`, both `unpaired` and `pairing`.

## Open v10 blockers — 3 (was 5)

| # | Real remaining scope |
|---|---|
| **#91** | Version, `MIGRATION-10.0.0.md`, README matrix + security note all landed, and the matrix now carries the split compatibility floor. Left: **`docs/SPEC-VERSION.md` (still absent)**, `encryption-and-linein-plan.md` §6/§8 corrections, NuGet README encryption section. Was gated on #89/#90. |
| **#90** | Items 1, 2, 3, 5 done. Item 4 is **partly** done: pairing now runs live against a current-spec server every push. Still missing — the interop client itself only implements two scenarios (`unpaired`, `pairing`, see `tools/interop/InteropClient/Program.cs:6`), so **source streaming and PIN pairing have still never been exercised against a real counterparty**. That needs new scenarios in the client, not just workflow lines. |
| **#89** | Items 1, 2 and 4 done. Left: **item 3 only** — `System.Text.Json 8.0.5` and `Microsoft.Extensions.Logging.Abstractions 8.0.2` on a net10 target. The build still emits `NU1510` for the STJ reference on every Release build, which is the concrete symptom. |

### Closed since the last edition

- **#124** (PR #142) — listener-path prologue now binds the received bytes.
- **#127** (PR #146) — pairing window, gating, escalation, attempt timeout, spec #130/#131.
- **#144** (PR #145) — cipher-suite probe now tests libsodium, not the BCL.

## Decisions Chris has made — do not relitigate

- **9.x stays maintained**, not EOL, until confident no more 9.x-applicable bugs surface.
- **The aiosendspin floor is split by capability, and is now settled with evidence:**
  connecting and playback (including unpaired access) work against **`>= 7.0.0`**; **pairing
  requires `>= 9.0.0`**. The long-suspected `>= 8.0.0` figure is **wrong** — 8.0.0 still sends
  the flat `selected_pair_method` and has no pairing-window handling. Verified by inspecting
  the 7.0.0, 8.0.0 and 9.0.0 releases directly. Recorded in the README matrix and
  `MIGRATION-10.0.0.md`.
- **Noise.NET stays** (#89 item 2 closed). The vetting review found no unaudited managed
  crypto — it is a ~50 KB wrapper that P/Invokes libsodium — and no advisories. The real risk
  was packaging: it declares `libsodium >= 1.0.16`, whose natives are x64/x86 only. Now pinned
  directly to `1.0.22` for ARM coverage, guarded by the `linux-arm64` AOT leg.
- **`shutdown`** on app exit; `restart` reserved for a future self-update path.
- PublishAot compatibility **will** be achieved before shipping — now proven on two
  architectures rather than asserted.
- Interop keeps a single pinned server (9.0.0) rather than a split matrix. Consequence
  accepted: **the 7.0.0 playback floor is no longer covered by CI.**

## Issues filed since the last edition

**#143** — `SendspinHostServiceArbitrationTests.BothDiscovery_LastPlayedNewServer_WinsTieAndExistingGetsAnotherServer`
hangs indefinitely on ~3% of runs and burns the whole CI job timeout. **Pre-existing**, measured
at main 2/75 vs branch 3/60. A hung run still prints `Passed!` with a reduced total, so check
the count and the exit code, never the word "Passed".

**#147**, **#148** — parked findings from #127's whole-branch review; both fail safe.

Still open from before: **#92** (upstream tracking) — its audit list names spec #122/#125/#126
and **predates #130 and #131**, which is what this session actually implemented. Update it when
`docs/SPEC-VERSION.md` is written; the pairing work targets spec `5b0e6469` + `21b97460`.

**#95** deserves promotion from "enhancement". The hand-maintained options mirror produced the
same silent-drop defect **three times in one branch** — `SendspinHostService.BuildClientOptions`,
`TestClientOptions`, then `BuildClientOptions` again with a different field, the last caught only
by the whole-branch review after a task existed specifically to guard it. The mirror test is now
structural (reflects over every option), so the hole is closed; the category is not.

## Loose end

`c:\CodeProjects\windowsSpin` on `feat/v10-sdk-surface` has an **uncommitted** fix in
`src/Sendspin.Windows/App.xaml.cs`: `OnExit` was `async void`, so WPF exited the process at
the first `await` and the SDK never got to send `client/goodbye`. Now synchronous with a
bounded 3 s wait via `Task.Run` (escapes the UI SynchronizationContext, so it cannot
deadlock). Chris had not verified it live yet. Also open: windowsSpin#65 — its rejection path
sends `already_connected`, which is not a spec reason; `concurrent_attempt` is.

**windowsSpin will need work for #127's breaking changes**: `PresentPinAsync` now takes a
`PinPresentation` (pin + language hint) rather than a string, and completing a `static_pin` or
gated `dynamic_pin` pairing now requires supplying a `PairingWindow` in options and calling
`Open()` from an operator gesture. Rebuild with:
`dotnet build Sendspin.Windows.sln -c Release -p:UseSdkSource=true -p:SdkSourcePath=c:\CodeProjects\SendspinSDK`

## Working notes that earned their keep

- **The recurring defect shape**: one fact stored in two places, only one of which gets
  updated. Now seen five times across two sessions — `LastServerActivate` vs
  `LastServerHello.ActiveRoles`, `WireFrame`'s two write paths, and the options mirror three
  times. Found by asking *what else reads or writes this fact*, never by asking *does this code
  work*.
- **A falling test count is louder than a failing one.** Tests that stop being *discovered* do
  not turn a run red — the summary still says `Passed!`, just with a smaller total. This bit
  twice: six tests were destroyed by an overwrite and caught only because 583 + 3 ≠ 580. Check
  the number after every change, not just the verdict.
- **Green can mean the test asserts the bug.** `CipherSuiteAvailabilityTests.IsSupported_TracksTheBcl`
  literally pinned the defect #144 describes. A passing suite defended the wrong contract.
- **Mutation-check anything load-bearing.** Setting the failure-counter reset to `77` left all
  612 tests passing — half of #127 was fixed but unpinned. Reviewers found this repeatedly by
  breaking the code and watching what failed to fail.
- **Per-task review cannot see seams.** Ten task reviews came back clean; the whole-branch
  review then found three Important defects, every one of them in state shared *between* tasks.
  Budget for the broad pass — it is not a formality.
- **A suspended `async` method has no thread stack.** When a stack dump of a hung process shows
  no test frames at all, that is not "nothing is running" — it is a test parked in `await`.
  Look for unbounded awaits, not blocked threads.
- **Three green runs is not a baseline.** With a ~3% flake (#143), a small clean baseline reads
  as stable and makes any new branch look like the cause. Use 40+ runs per side before
  attributing a flake.
- **Mirror harnesses cannot catch systematic errors.** Still true, and worth re-checking each
  time a test double grows: #127's end-to-end test drives CPace with the real SDK type in the
  *opposite* role rather than a hand-rolled peer, but it does share `PinPairing.BuildSid` with
  the client — covered only because separate KAT tests pin that against the reference
  implementation.
- **Check issue/PR state before planning against it.** #124 and #127 were both filed against a
  spec that had already moved; #127's real scope was roughly double what it described.
