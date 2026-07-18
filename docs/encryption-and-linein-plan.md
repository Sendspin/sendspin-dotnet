# Sendspin .NET SDK — Implementation Plan: Encryption & Line-In (Source Role)

*Research basis: `Sendspin/spec` @ `7c04eb7` (2026-07-14, "Keep the connection open across pairing attempts (#120)") — designated source of truth. The public website and older SDK docs describe a superseded pre-encryption draft.*

---

## 1. What the research found

### The headline

Both features are **already fully specified at spec HEAD** — this is not "upcoming ideas," it is a shipped spec revision the SDK does not implement yet:

1. **Encryption is mandatory.** All connections use end-to-end Noise Protocol encryption (`KKpsk2`), with Curve25519 identities, a PSK/pairing system (three pairing methods), trust levels, activities, and a management message family. The entire connection bootstrap is different from what the SDK implements today.
2. **Line-in is the `source@v1` role.** A client captures local input (AUX/line-in, turntable, mic, Bluetooth receiver), encodes it, and streams timestamped chunks *to* the server; the server mixes/distributes to players. It is deliberately simple — but it **requires a paired (encrypted, `user`-trust) connection**, so encryption is a hard prerequisite.

### Encryption, as specified

- **Handshake sequence** (replaces today's plaintext hello exchange):
  `client/init` (cleartext text frame: `client_id`, `version: 1`, `suite`) → `server/init` (`server_id`, `version`) → `noise/handshake` ×2 → **switch to Noise transport mode: every subsequent frame is a binary AEAD ciphertext** → `server/hello` (just `name`) → `client/hello` (roles, capabilities, `trust_level`, `supported_pair_methods`, `unpaired_access`) → `server/activate` (`activities`, `active_roles`, `selected_pair_method`).
- **Pattern/crypto:** Noise `KKpsk2`; **server is always the Noise initiator** (regardless of who opened the WebSocket). Suites: `25519_ChaChaPoly_SHA256` and `25519_AESGCM_SHA256` (client picks one, servers support both). `client_id`/`server_id` **are** the base64url Curve25519 static public keys (43 chars) — identity and crypto key are the same thing, and must persist across reboots.
- **PSK system:** long-term per-pair PSK (created by pairing), Pairing PSK (bootstrap secret via QR/copy), and a **published Sentinel PSK constant** used pre-pairing. Selection via `psk_id = base64url(SHA-256("sendspin-psk-id-v1" || PSK))` carried in Noise message 1. Prologue = exact wire bytes of `client/init` + `server/init`.
- **Framing:** after handshake, JSON messages travel as binary type `0`; fragmentation message types `2`/`3` handle the Noise 65,535-byte ceiling (max 65,518 payload bytes/frame) — required for large artwork and hi-res PCM/FLAC audio chunks.
- **Trust & access:** `trust_level` `user` (pairing record exists) vs `none`; **unpaired access** is an explicit client toggle allowing playback on the Sentinel PSK (MITM-exposed, confidentiality only). `server/activate` activity sets (`playback`/`pairing`/`management`) are constrained by which PSK matched; violations close the connection with new `client/goodbye` reasons (`unauthorized`, `pairing_required`, `concurrent_attempt`, `unpaired`).
- **Pairing:** three methods. **Pairing PSK is mandatory for clients**; `dynamic_pin` and `static_pin` (CPace PAKE: CPACE-X25519-SHA512, commit/reveal PIN derivation, PSK wrapping, lockout counters) are **optional for clients**. After pairing, the server **re-handshakes in-band** to the new PSK without closing the WebSocket.
- **Management messages are mandatory for all clients:** `management/list-records`, `add-record`, `remove-record`, `get/set-pairing-config`, `management/result` (+ storage accounting), plus `server/unpair`.
- **Multi-server arbitration** is now ranked by declared activities (`management` > `playback` > `pairing` > empty), with last-playback-server persistence — replacing the old `connection_reason` ranking the SDK implements.

### Line-in (`source@v1`), as specified

- Role `source@v1`: "captures audio from a local input and streams it to the server." A device MAY be both `source` and `player` (e.g., a speaker with an AUX input) but **MUST NOT play its captured input locally** — it only plays what the server distributes, keeping the group in sync.
- **Capabilities:** `source@v1_support: { features: { line_sense?: bool } }` in `client/hello`.
- **Control:** server-driven only. Default is stopped; server sends `server/command { source: { command: "start" | "stop" } }`. Client responds with `client_stream/start` (`codec`/`channels`/`sample_rate`/`bit_depth`/`codec_header?`) → binary type-`12` chunks → `client_stream/end`. Deactivating the role via `server/activate` also stops streaming.
- **Chunk format:** `[type 12][int64 BE timestamp][encoded audio]` where the timestamp is the **capture time converted to the server time domain** via the time filter. A source MUST NOT report `available: true` until its clock filter has converged.
- **Line sensing:** optional `client/state { source: { signal: 'present' | 'absent' } }` — a hint the server MAY use to auto-start/stop.
- **Trust gating:** sources stream potentially-sensitive audio (mic/line-in), so `source@v1` MUST only be activated on a paired connection; the client MUST refuse to stream at trust `none`.
- Codecs: server must accept `opus`, `flac`, and `pcm` from sources.

### In-flight spec changes to watch (not yet merged)

| Item | Status | Impact on us |
|---|---|---|
| PR #122 "Resolve more pre-1.0 spec gaps" | Draft, active (updated 2026-07-17) | Touches **codec framing and source format/trust rules** — re-read before implementing Phase 4 (source) |
| PR #114 README split | Open | Editorial only; changes where spec text lives |
| PR #80 Lyrics role | Open, stale base | Wants binary IDs 12–13, which **collide with the source role's 12–15 allocation** at HEAD — will need reallocation upstream; nothing for us to do but don't hardcode assumptions that 13–15 stay free |
| Issues #88–#93 (cross-SDK conformance audit) | Open | Name `sendspin-dotnet` gaps directly: no multi-server last-played persistence, wrong default goodbye reason (`user_request`; audit recommends `restart`). Cheap fixes to fold into Phase 2 |

---

## 2. Where the SDK stands today (gap analysis)

| Area | SDK today | Spec @ HEAD |
|---|---|---|
| Transport | Plaintext `ws://`, JSON text frames + raw binary frames | Noise-encrypted; all post-handshake frames are binary ciphertexts |
| Handshake | Client sends `client/hello` first (with `client_id`, `version`); `server/hello` carries `server_id`, roles, `connection_reason` | `client/init`/`server/init` + Noise, then `server/hello` (name only) → `client/hello` → `server/activate` |
| Identity | Arbitrary `client_id` string | `client_id` = persistent Curve25519 public key |
| Auth/pairing | None | PSK records, 3 pairing methods, trust levels, management messages |
| Multi-server | Ranked by `connection_reason` (`playback` > `discovery`) in `SendspinHostService` | Ranked by activities; last-playback persistence required |
| Fragmentation | None | Types 2/3, 65,518-byte payload cap |
| Binary IDs | Audio 4–7, artwork 8–11, visualizer 16–21 (matches spec) | Same, plus source 12–15, fragments 2–3, JSON 0 |
| Audio direction | Receive/decode only (PCM/FLAC/Opus decoders) | Source role adds capture → encode → send |
| `client/state` | `player` object, external-source via dedicated methods | `available` + `player` + `source` objects (maps cleanly) |

Existing assets that carry over largely intact: `MessageSerializer` (JSON source-gen — payload shapes change, envelope doesn't), `BinaryMessageParser` (applies to decrypted plaintext), `KalmanClockSynchronizer`/time filter (spec now mandates exactly this), `AudioPipeline` (player side unchanged), mDNS listener/advertiser, reconnect/backoff logic, and the whole test harness pattern (fake connection + raw JSON).

---

## 3. Implementation plan

Encryption must land first: it rewrites the connection bootstrap that everything rides on, and the source role is spec-gated on paired connections. Proposed order — five phases, each shippable:

### Phase 0 — Groundwork (small)

- **Crypto dependency spike.** Evaluate Noise libraries for .NET — primary candidate: the pure-managed Noise Protocol implementation from `github.com/Metalnem/noise` (supports KK + psk modifiers, 25519, ChaChaPoly, AESGCM, SHA256; no native deps → keeps NativeAOT story). Fall back to a small in-repo Noise state machine over BCL `AesGcm`/`ChaCha20Poly1305` plus a managed X25519 if the library fails vetting (maintenance, AOT, allocation profile). Deliverable: a console spike that completes `KKpsk2` against `aiosendspin` on a LAN, validating the psk_id/prologue/sentinel constants from the spec.
- **Pinned interop target: `aiosendspin==7.0.0`** (released 2026-07-15) — the first release with the encryption stack (`noiseprotocol`, `cpace`, `cryptography` deps; `aiosendspin/noise/` package with driver, pairing incl. CPace PIN methods, trust store with unpaired access). Constants verified to match the spec byte-for-byte (Sentinel PSK, `sendspin-psk-id-v1` label, fragment types 2/3, 65,519 transport cap). The server side is Noise-wired too: `pip install "aiosendspin[server]==7.0.0"` is the test counterparty. Versions ≤ 6.1.1 are plaintext-only — keep 6.1.1 around as the A/B case for old-server failure behavior.
- **Layering refactor (no behavior change).** Today `SendspinConnection` hands text/binary frames straight to `SendspinClientService`. Introduce a **secure-channel/framing layer** between them (`ISendspinFrameChannel`: send/receive *plaintext application frames*, owns handshake state, encryption, fragmentation). `SendspinClientService` and all existing tests keep talking JSON/plaintext binary; `FakeSendspinConnection` moves up to fake this layer, so the entire existing test suite survives the transport rewrite.
- Pin a spec commit hash in the repo (e.g., `docs/SPEC-VERSION.md`) and note the PR-#122/#114 watch items.

### Phase 1 — Noise transport + new handshake (large; the breaking release)

- Implement `client/init` / `server/init` / `noise/handshake` cleartext exchange, prologue binding, suite selection (`25519_ChaChaPoly_SHA256` first; add AESGCM via BCL `AesGcm` where supported).
- Noise transport mode: binary-frame encryption/decryption, type-byte-0 JSON routing, **fragmentation (types 2/3)** with the 65,518-byte payload cap — including reassembly rules and the one-in-flight constraint.
- **Identity management:** new `ISendspinIdentityStore` (persist the Curve25519 keypair; `client_id` derived from it). Default file-based implementation + DI hook for platform stores (DPAPI/Keychain/keystore left to the app, like `IStaticDelayStore` today).
- **PSK store:** `IPairingRecordStore` (records = PSK + optional `server_id` + used flag + category tag), Sentinel PSK constant, `psk_id` computation, stored-pubkey vs shared-PSK verification models.
- New hello flow: encrypted `server/hello` → `client/hello` (new payload: `trust_level`, `supported_pair_methods`, `unpaired_access`, role supports) → `server/activate` handling (activities, `active_roles`, admissibility checks, close-with-reason rules).
- Re-handshake support (in-band key rotation; must also handle the post-pairing promote path even before pairing ships, since servers may rotate keys on long-running connections).
- **Unpaired access** toggle in `ClientCapabilities`/options — this is the bridge that lets the SDK talk to new servers before pairing UX exists (Sentinel PSK + `unpaired_access.enabled`), and the right default ship-state for existing consumers (opt-in, documented MITM caveat per spec).
- Failure handling per spec: close-without-error on any handshake failure, 30 s phase timeouts, handshake-failure backoff.
- **SemVer major** (v10.0.0 by current cadence) + `MIGRATION-10.0.0.md`. Old plaintext protocol support should be dropped, not dual-stacked — the spec has no downgrade negotiation, and `version: 1` in `client/init` is the only versioning.

### Phase 2 — Activities, trust, and connection-lifecycle conformance (medium)

- Replace `connection_reason` arbitration in `SendspinHostService` with the activities ranking (`management` > `playback` > `pairing` > empty), the pairing-attempt non-displacement rule, the 30 s provisional-connection timeout, and **last-playback-server persistence** (new small store interface; also closes conformance-audit issue #92's dotnet finding).
- New `client/goodbye` reasons; change the SDK's default disconnect reason from `user_request` to spec-aligned behavior (audit issue #91).
- `client/state` reshape: top-level `available` flag (map `EnterExternalSourceAsync`/`ExitExternalSourceAsync` onto it), delta-update rules.
- Public API: surface `TrustLevel`, `Activities`, and activation changes as events (`ServerActivateReceived` or fold into `ConnectionStateChanged` metadata).

### Phase 3 — Pairing (Pairing PSK) + management (medium)

- **Pairing PSK flow only** (the client-mandatory method): generate/display the client's Pairing PSK + `client_id` (QR/string — SDK exposes the payload, app renders it), `client/pair-finalize` / `server/pair-finalize`, record persistence, post-pairing re-handshake, `pair/abort` handling, attempt timeout.
- `server/unpair` and the full **management family** (list/add/remove records, get/set pairing config, `management/result`, storage accounting) — spec marks these required for all clients. Storage accounting can report unbounded (omit `storage`) for desktop-class clients.
- Defer **PIN methods** (`dynamic_pin`/`static_pin`): they need CPACE-X25519-SHA512 (draft-irtf-cfrg-cpace: Elligator2 map, MCF tags), commit/reveal PIN derivation, PSK wrapping, and lockout persistence. Spec explicitly allows clients to ship without them. Track as Phase 5 if consumers (e.g., devices with displays wanting "show a PIN" UX) ask for it.

### Phase 4 — Line-in: the `source@v1` role (medium)

Mirror of the existing player pipeline, outbound:

- **Capture abstraction:** `IAudioCaptureDevice` (open format, start/stop, delivers PCM buffers with local capture timestamps) — implemented by the consuming app per platform (WASAPI loopback/line-in on Windows, etc.), same pattern as the current `IAudioPlayer`.
- **Encoder path:** `IAudioEncoder` + factory. Ship **PCM first** (trivial, spec-guaranteed server support), then Opus (bandwidth; check whether the existing Opus binding exposes encode — otherwise a managed encoder like Concentus, weighed against the NativeAOT/native-dep policy). FLAC encode is optional and low value for live capture.
- **`SourceStreamPipeline`:** capture buffers → encode → timestamp via the shared time filter (local capture time → server domain) → type-`12` binary frames through the secure channel; `client_stream/start`/`end` bracketing; obey `server/command source start/stop`, role deactivation, and `available` gating (clock convergence before `available: true`).
- **Trust enforcement:** refuse to activate/stream at trust `none` (hard-fail with a clear SDK error, per spec MUST).
- **Line sense:** optional `SetSignalPresence(present)` API → `client/state.source.signal`, advertised via `features.line_sense`.
- Capabilities/API: `ClientRoles.Source`, `SourceSupport` on `ClientCapabilities`, events for start/stop requests so the app can light up "streaming" UI.
- **Re-check PR #122 before starting** — it explicitly touches source format and trust rules.

### Phase 5 (optional/backlog) — PIN pairing methods

CPace PAKE implementation (or vetted library), dynamic-PIN out-channel plumbing (the SDK can emit the derived PIN to the app for display; audio-emission is app territory), static-PIN pairing-window gesture API, lockout counters with persistence.

---

## 4. Testing & verification strategy

- **Keep the existing pattern:** the framing-layer fake (Phase 0) preserves raw-JSON-driven tests for all role/message logic, including all 216 existing tests.
- **Crypto vectors:** unit-test the spec's published constants (Sentinel PSK bytes, its `psk_id`, the psk_id derivation label) and Noise `KKpsk2` against known-answer vectors; round-trip fragmentation at boundary sizes (65,518, 65,519, multi-frame artwork).
- **Loopback integration:** in-process client ↔ test server speaking real Noise over the real WebSocket path (both connection directions — the server-is-initiator rule is easy to get backwards on the server-initiated WS path).
- **Interop:** CI job (or at least a documented manual gate) running `aiosendspin` as the counterparty for handshake, pairing-PSK, re-handshake, and source streaming; this is the real conformance bar, and the conformance-audit issues show upstream is actively auditing SDKs (`Sendspin/conformance`).
- **Source pipeline:** fake capture device feeding a known waveform; assert chunk timestamps against a fake clock filter; assert stop-on-`stop`/deactivation/trust-drop.

## 5. Risks & open questions

1. **Pre-1.0 spec drift.** PR #122 amends codec framing and source trust/format rules; the README is being restructured (#114). Mitigation: pin the spec hash per release, re-audit the delta before each phase, keep an eye on `Sendspin/conformance`.
2. **Crypto dependency vetting.** A pure-managed Noise library needs a maintenance/security review before it becomes a hard dependency of every consumer; the fallback (in-repo Noise over BCL AEADs) costs more but is auditable. Decide in the Phase 0 spike. `ChaCha20Poly1305`/`AesGcm` BCL availability varies by platform — suite choice may need runtime detection.
3. **Hard compatibility break.** v-next speaks only the encrypted protocol; consumers on old servers can't upgrade until their server does. The migration doc and a clear README compatibility matrix are part of Phase 1's definition of done.
4. **Key/PSK storage security** is delegated to apps via the store interfaces; the SDK ships file-based defaults with documented caveats. Worth a security note in the README.
5. **Windows client impact** (windowsSpin): identity persistence, pairing UX (QR display), unpaired-access toggle, and — for line-in — a WASAPI capture implementation are app-side work items to schedule alongside Phases 1–4.
6. **Open upstream question worth filing:** the spec is silent on how a *client-initiated* connection learns it should retry after `pairing_required`; and lyrics PR #80's binary-ID collision with source confirms IDs 12–15 shouldn't be assumed stable until 1.0.

## 6. Optional: version gating & downgrade (decision pending — NOT committed to)

How does a new-SDK client behave against a pre-encryption (aiosendspin < 7.0.0) server? Two options; **no decision made yet**, and the default assumption is Option A unless real-world migration pain forces Option B.

**Groundwork facts (measured against real servers):**

- There is **no in-band version negotiation**: mDNS TXT carries only `name`/`path`, and the spec's `version` field only exists inside the new protocol. The presence or absence of a `server/init` reply *is* the version signal.
- A pre-7 server that receives `client/init` **closes the WebSocket immediately** (verified against aiosendspin 6.1.1: unknown-type deserialization error → close code 1000, no reply; `tools/probe_legacy_server.py`). Detection is therefore fast and deterministic — one throwaway connection, ~one RTT.
- The reference client does not fall back: aiosendspin 7.0.0's client is encrypted-only. Only its **server** dual-stacks (first-message sniffing behind an operator `allow_unencrypted` flag) — which means old *clients* keep working against new servers, but not the reverse.

### Option A — clean break (baseline assumption)

SDK vX+ (the encryption release) speaks only the encrypted protocol. Connecting to a pre-7 server fails with a clear, documented error ("server does not support Sendspin encryption; upgrade the server or stay on SDK v9.x"). Consumers pinned to old servers stay on the v9.x line (kept on security-fix support for a defined window).

- Pros: matches the reference implementation's posture; zero downgrade-attack surface; no dual-stack maintenance; the spec itself has no downgrade story to conform to.
- Cons: hard cutover for apps whose users control the server version (e.g., MA installs that lag); support burden shifts to "which SDK line do I need?"
- Cost: near zero — this is what Phase 1 produces naturally. Requires only a good error message (distinguish "closed during handshake" → probable legacy server) and README compatibility matrix.

### Option B — probe-first with policy-controlled fallback (only if needed)

Open every connection with `client/init`; on close/timeout before `server/init`, reconnect speaking the legacy plaintext protocol, governed by `ConnectionOptions.EncryptionMode`:

- `Required` (long-term default) — never fall back.
- `Preferred` (migration default) — fall back with a loud warning.
- `Legacy` — plaintext only, for pinned old deployments.

Plus two **non-configurable** anti-downgrade rules: (1) any endpoint that has ever completed a Noise handshake never falls back again (persisted per endpoint); (2) a server with a pairing record is always `Required`. Wrinkle: on server-initiated connections the client speaks first blind, so fallback there needs per-remote alternation across the old server's retry loop — workable but ugly.

- Pros: seamless migration window; auto-promotes when the server upgrades (always-probe-first is cheap).
- Cons: dual-stack maintenance for a full major cycle; downgrade-attack surface that must be actively defended; extra state (per-endpoint protocol memory); more test matrix.

**Decision trigger:** revisit at Phase 1 exit. If MA's stable channel is already shipping a 7.x-line server by then (their server keeps `allow_unencrypted` for old clients, so upgrade pressure is one-directional), Option A stands. Option B only earns its complexity if a significant consumer base demonstrably cannot upgrade servers on our timeline.

## 7. Phase 0 spike — COMPLETED (2026-07-18)

Ran against a real `aiosendspin[server]==7.0.0` instance (`spike_noise_handshake.py`), with the client side built **only from raw primitives** (X25519 + a Noise KKpsk2 state machine + hand-built JSON — no aiosendspin client code), i.e., the exact layering the .NET implementation will use. All checks passed first try:

- Spec constants verified: Sentinel PSK bytes and the published sentinel `psk_id` (`GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo`) both derive correctly.
- Full bootstrap verified end-to-end: `client/init` → `server/init` → `noise/handshake` ×2 (prologue = exact init wire bytes, PSK mixed after msg 1's `psk_id`) → transport mode → decrypted `server/hello` (binary frame, type byte 0) → encrypted `client/hello` (with `client_id`/`version` correctly omitted) → `server/activate`.
- Activity gating confirmed: sentinel PSK + `unpaired_access.enabled: false` → `activities: []`, `active_roles: []`, exactly per the spec's admissibility table.
- 7.0.0 implementation notes for the .NET port: `client/hello` under encryption omits `client_id`/`version` (legacy plaintext clients still send them — 7.0.0 retains an `allow_unencrypted` server flag and legacy-hello path, useful for our own transition testing); Noise msg 2's payload is literally `{}`.

Remaining Phase 0 item: the .NET-side library evaluation (Metalnem Noise vs. in-repo state machine) — not runnable in this session (no .NET SDK available in the sandbox), but the wire contract above is now fully de-risked and the spike script doubles as the reference test harness.

## 8. Phase 1 exit status (2026-07-18)

Phases 0, 1, and 3 are COMPLETE (PRs #62-#66): Noise KKpsk2 transport with
fragmentation and in-band re-handshake, server-driven hello/activate flow with
admissibility enforcement, Pairing PSK flow with persistent record stores, and the
management message family + server/unpair. 260 tests; full-stack E2E verified against
aiosendspin 7.0.0.

**Deliberately deferred — tracked as repo issues:**

1. **PIN pairing methods (CPace)** — `dynamic_pin`/`static_pin` need
   CPACE-X25519-SHA512 (draft-irtf-cfrg-cpace), commit/reveal PIN derivation, PSK
   wrapping, and lockout persistence. Spec-optional for clients ("clients must
   implement Pairing PSK and may additionally implement either or both PIN methods").
   Needed before devices with displays can offer "show a PIN" pairing UX.
2. **Live pairing interop harness** — CI (or documented manual) run of the Pairing PSK
   flow + post-pairing re-handshake against a real `aiosendspin[server]==7.0.0` with a
   server-side staged Pairing PSK. Unit/loopback coverage exists; the live
   counterparty run is the remaining conformance gate.

**Phase 2 — COMPLETE.** Landed across two efforts: PR #53 (updated pre-merge)
delivered the pure arbitration decision table, `ILastPlayedServerStore` persistence
(closing audit finding #92), the loopback FakeServer harness, and the spec-current
rules (activity ranking, higher-or-equal admission, pairing non-displacement,
empty-tie last-playback gate, `concurrent_attempt` for rejected incomings). The
follow-up PR added encrypted-mode support to `SendspinHostService` (per-connection
Noise framing, server-driven hello, 30 s provisional-activate window) and feeds
arbitration priority from declared `server/activate` activities, falling back to the
legacy `connection_reason` mapping. Audit finding #91 (default goodbye reason) was
verified already satisfied — the SDK defaults to `restart`.

## 9. Suggested immediate next steps

1. Phase 0 spike: clone-and-test a Noise `KKpsk2` handshake against `aiosendspin`; validate sentinel constants. (~1–2 days)
2. Land the framing-layer refactor behind the current protocol (pure refactor PR, no wire change).
3. File tracking issues in `sendspin-dotnet` per phase, referencing spec sections and this plan.
4. Fix the two cheap conformance-audit findings (#91 default goodbye reason, #92 arbitration persistence) — they're independent of the crypto work and reduce the Phase 2 diff.
