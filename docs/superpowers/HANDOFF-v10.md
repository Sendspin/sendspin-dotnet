# v10.0.0 handoff — state as of 2026-08-10

Written to survive a context clear. Everything below was verified against the repo, not
recalled. **Verify again before planning against it** — three separate "this is blocked"
premises turned out to be stale within a single session.

## Repo state

- `main` = `2e1824e`, carries everything. **Zero open PRs.**
- Version: `<Version>10.0.0</Version>`. Nothing published — CI publishes only on a `v*.*.*`
  tag push (`build.yml`, publish job gated `if: startsWith(github.ref, 'refs/tags/v')`), and
  the tag name is authoritative, overriding the csproj via `-p:Version=`.
- Suite: 582 tests green on `net10.0`. Release clean on `net8.0` + `net10.0`.
- **Zero IL2026/IL3050.** A CI `aot` job publishes `tools/AotSmokeTest` with `PublishAot` on
  `linux-x64` and runs the binary on every push.

## Open v10 blockers — 5

| # | Real remaining scope |
|---|---|
| **#127** | Untouched, largest. Dynamic-PIN failure counter is a terminal lockout, not the spec's escalation — and it **deadlocks** (counter only resets on a successful `server_kc`, which cannot happen once the client refuses to start the attempt). Fixing it means building the pairing window the SDK has never had: window state, `client/pair-pending`, `management/open-pairing-window`, gating policy (static PIN always; dynamic when escalated or `pin_length < 6`). Also remove the non-spec `locked_out` abort reason. |
| **#124** | Untouched, real. Prologue byte fidelity not end-to-end on the **listener** path; needs a raw-bytes callback for text frames on the socket wrapper — the path MA actually uses. `HandshakeByteFidelityTests` pins the outgoing side already; #124 is the same property inbound. |
| **#91** | Version, `MIGRATION-10.0.0.md`, README matrix + security note all landed. Left: `docs/SPEC-VERSION.md`, `encryption-and-linein-plan.md` §6/§8 corrections, NuGet README encryption section. Gated on #89/#90. |
| **#90** | Items 1, 2, 3, 5 done. Left: 64 MB `MaxReassembledMessageBytes` cap (**skipped deliberately** — the 128 KB pre-first-message cap already covers that branch for ~1000 frames less crypto) and the live `aiosendspin` interop gate (CI work). |
| **#89** | Item 1 done (0 IL warnings). **Item 2 is a decision for Chris**: Noise.NET's *technical* AOT-safety is now proven empirically, the maintenance/security review is not. Item 3 (refresh `System.Text.Json 8.0.5`, `Logging.Abstractions 8.0.2` on a net10 target) untouched. |

## Decisions Chris has made — do not relitigate

- **9.x stays maintained**, not EOL, until confident no more 9.x-applicable bugs surface.
- **`aiosendspin >= 7.0.0`** is the compatibility floor for now; the `>= 8.0.0` figure concerns
  pairing specifics and backporting, still being pinned down.
- **`shutdown`** on app exit; `restart` reserved for a future self-update path.
- PublishAot compatibility **will** be achieved before shipping, so the README claim stands.
- Order of work was #90 → #89 → #124.

## Loose end

`c:\CodeProjects\windowsSpin` on `feat/v10-sdk-surface` has an **uncommitted** fix in
`src/Sendspin.Windows/App.xaml.cs`: `OnExit` was `async void`, so WPF exited the process at
the first `await` and the SDK never got to send `client/goodbye`. Now synchronous with a
bounded 3 s wait via `Task.Run` (escapes the UI SynchronizationContext, so it cannot
deadlock). Chris had not verified it live yet. Also open: windowsSpin#65 — its rejection path
sends `already_connected`, which is not a spec reason; `concurrent_attempt` is.

To rebuild windowsSpin against the SDK:
`dotnet build Sendspin.Windows.sln -c Release -p:UseSdkSource=true -p:SdkSourcePath=c:\CodeProjects\SendspinSDK`

## Issues filed this session

#127 (v10-blocker), #128, #129, #130 (**security**, pre-existing, high priority — `add-record`
does not reject the Sentinel PSK, so a paired server can plant a record that shadows it and
every anonymous peer authenticates at `user`), #131, #132, #135, and windowsSpin#65.
Closed: #81 and #82, both already-fixed-but-open.

## Working notes that earned their keep

- **The recurring defect shape**: one fact stored in two places, only one of which gets
  updated. `LastServerActivate` reset while `LastServerHello.ActiveRoles` — written from the
  same payload 15 lines later — was not, leaving the microphone gate open. Found by asking
  *what else reads or writes this fact*, never by asking *does this code work*.
- **Mirror harnesses cannot catch systematic errors.** `TestNoiseServer.EncryptFragmented`
  mirrors `EncryptOutbound`; with a 1-byte ceiling error the new hand-computed vectors failed
  3/7 while 21 existing round-trip tests passed. Same trap one layer down for the handshake
  JSON, which is why `HandshakeByteFidelityTests` was written against the *old* output first.
- **Green ≠ covered.** A tautological assertion (`x is null ? y : y` vs `y`) and a degenerate
  fixture (identity clock) fail identically: silently. Mutation-check anything load-bearing.
- **Check issue/PR state before planning against it.** #81, #82, #90 items 2+5, and #91's
  gate list were all stale. See `check-pr-status-before-pushing` in memory.
