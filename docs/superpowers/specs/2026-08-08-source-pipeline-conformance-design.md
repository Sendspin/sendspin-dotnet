# Design: source pipeline correctness and conformance

*Closes [#82](https://github.com/Sendspin/sendspin-dotnet/issues/82). Larger than the other remaining blockers: two correctness defects plus a spec-conformance sweep.*

## 0. Two premises in the issue are stale, and one gap it does not mention

**Item 4 is unblocked.** The issue says the clock-convergence gate on `available: true` is "blocked on #77 (no `available` field exists)". [#113](https://github.com/Sendspin/sendspin-dotnet/pull/113) shipped `available`, and [#116](https://github.com/Sendspin/sendspin-dotnet/pull/116) settled how synchronization composes into it (`ClockSyncEstablished` — latched at first convergence, so jitter cannot flip it). So the availability interaction can be done now.

**Item 3's delta list describes a draft.** Spec #122 merged 2026-07-31; the issue summarised it while open and warned "re-read at merge before finalising". This design works from the merged text of `roles/source/v1.md`, not the issue's summary.

**The trust requirement is already satisfied — verify, do not re-implement.** `roles/source/v1.md:10` is a security MUST the issue never mentions:

> A source captures potentially sensitive audio (microphone, line-in), so `source@v1` MUST only run on a paired connection (trust level `user`) and a source client MUST NOT stream when the trust level is `none`.

`IsSourceStreamingPermitted()` already requires `MatchedPsk?.Category == PskCategory.LongTerm`, which *is* trust `user`. The work here is a test that pins it, plus checking the "refuses it and closes the connection" half on activation.

**A gap the issue does not list: drift is applied conditionally.** `KalmanClockSynchronizer.ClientToServerTime` returns

```csharp
clientTime + (IsDriftStatisticallySignificantUnsafe() ? _offset + _drift * elapsed : _offset)
```

so drift is omitted until the estimate is statistically significant, with the rationale that early drift estimates are noise. `roles/source/v1.md:8` is explicit: *"apply both offset and drift, not offset alone."* Section §3 rules on this.

## 1. Chunks must be encoded and framed in capture order

`SourceStreamPipeline` fires `SourceChunkAsync` per captured buffer through `SafeFireAndForget`, so two overlapping captures produce two concurrent tasks that both call `encoder.Encode` and `_sendBinaryAsync`.

Both are unsafe. `IWireFraming` is documented as not thread-safe, and — as [#123](https://github.com/Sendspin/sendspin-dotnet/pull/123) established the hard way — an encrypting framing driven from two threads races its AEAD nonce counter. `PcmSourceEncoder` may be stateless, but Opus will not be, and the interface does not promise thread safety.

**Feed captures through a single-consumer channel and drive encode and send from one consumer.** A bounded channel, because an unbounded one turns a stalled send into unbounded memory growth — and the spec already tells us what to do when we fall behind (§4).

This is also where the spec's backlog rule belongs, since a single consumer is what makes "drop the backlog" expressible at all.

## 2. A failed encoder must not wedge the pipeline

`SourceStreamPipeline`'s start path has no `try`/`finally`. If `_encoderFactory.Create` throws, `_streaming` stays `true`, no `client_stream/end` is sent, and the pipeline is permanently stuck: a later `start` is ignored as already-streaming, and a `stop` sends an end for a stream that never began.

Restore the pre-start state on any failure, and surface the failure rather than swallowing it. Note the interaction with §3's idempotence: "already streaming" must mean *actually* streaming, so the flag has to be set only once the stream is genuinely open.

## 3. Command semantics, from the merged spec

`roles/source/v1.md`:

| Rule | Line |
|---|---|
| `start` while the stream is open MUST NOT restart it; `stop` while stopped is ignored | 44 |
| Default after handshake is `stop`; a source MUST NOT stream until the server sends `start`; the server is the only initiator | 48 |
| An unsolicited `client_stream/start` is a **protocol error** — the server should close the connection | 48 |
| Streaming state is **per-connection**: a previous `start` does not survive reconnection | 50 |
| Clients MUST send `client_stream/start` before the first chunk | 88 |

Idempotence and the per-connection reset are the substance. The reset matters most: after a reconnect the client must not resume streaming on the strength of a `start` from the previous connection, and — given #123 — a reconnect is exactly what a post-pairing promote looks like.

**On the drift question.** The spec's sentence is normative and the current conditional is a deviation. But applying a noise-dominated drift estimate to a *timestamp* is not obviously better than omitting it, and the existing comment shows the conditional was a deliberate choice. Two defensible readings, so: **apply both terms unconditionally, matching the spec**, and keep the significance test only as a diagnostic. The spec's own note (line 98) tells servers to expect discontinuous timestamps and to resample rather than trust per-chunk cut points, which is precisely the tolerance that makes early-drift noise survivable. Conforming is cheap; diverging silently is not.

Also check the inverse itself. The spec asks for the `t_server` that maps to `t_capture` under the server-to-local mapping. `ClientToServerTime` evaluates its drift term at `clientTime`, which approximates rather than solves the inverse. The magnitude is small — tens of microseconds per second of elapsed time — but the fix is arithmetic, not architecture, and worth doing while in the file.

## 4. Chunk bounds, backlog, and the availability interaction

| Rule | Line |
|---|---|
| A source MUST NOT send a chunk longer than **150 ms**, SHOULD NOT send one shorter than **5 ms** (final chunk before an end MAY be shorter) | 96 |
| After a network stall, drop buffered backlog beyond a small bound and resume from live capture rather than burst stale audio | 96 |
| Server rejects chunks when there is no open input stream, or when the client is not `available` | 87 |

The `available` interaction is new work enabled by #113/#116: when availability goes false the client must send `client_stream/end` **first**, and the server treats it as an implicit stop. That has to hook the single availability publisher #116 established, not a second path.

The 150 ms ceiling is a MUST and depends on the capture device's buffer size, which the SDK does not control — so it must be enforced by splitting oversized captures, not by assuming they never happen.

## 5. Out of scope

| Deferred | Reason |
|---|---|
| Opus and FLAC framing rules (one packet per chunk; whole FLAC frames plus `codec_header`) | Neither encoder exists yet. The rules belong with the encoder that implements them; PCM's "any whole number of interleaved frames" is what applies today |
| `bit_depth` ignored for `opus` | Same |
| [#118](https://github.com/Sendspin/sendspin-dotnet/issues/118) pairing quiescence | A source pipeline streaming through a pairing window is listed there |
| [#124](https://github.com/Sendspin/sendspin-dotnet/issues/124) listener byte fidelity | Unrelated path |

Package version stays `9.2.0`; [#91](https://github.com/Sendspin/sendspin-dotnet/issues/91) owns the release.

## 6. Testing

Standing bar: any test asserting something did *not* happen must survive "would this pass if the machinery producing it were deleted?" It has found a real gap in every slice on this repo.

| Item | Test | The assertion that matters |
|---|---|---|
| §1 | two overlapping captures | chunks reach the framing in **capture order**, and the encoder is never entered concurrently — assert on observed ordering, not on absence of a crash, since a stateless PCM encoder will not crash |
| §1 | a stalled send with captures continuing | the channel is bounded: memory does not grow without limit, and the drop rule fires |
| §2 | `Create` throws | `_streaming` is false afterwards, no `client_stream/end` was sent, **and a subsequent `start` succeeds** — the last clause is the wedge; without it the test passes on a pipeline that is still stuck |
| §3 | `start` while open | no second `client_stream/start`; `stop` while stopped sends no `client_stream/end` |
| §3 | reconnect after a `start` | streaming does **not** resume; a fresh `start` is required. Positive control: after the fresh `start` it does stream |
| §3 | trust level `none` | streaming is refused. Pins the existing gate so a refactor cannot silently drop a security MUST |
| §3 | drift applied | a known offset and drift produce a timestamp including **both** terms; a drift-only-when-significant implementation fails it |
| §4 | a capture longer than 150 ms | it is split; no chunk on the wire exceeds the ceiling |
| §4 | availability goes false while streaming | `client_stream/end` is sent, and it precedes the `client/state` carrying `available: false` |

## 7. Success criteria

1. Captures are encoded and framed by a single consumer, in capture order, through a bounded channel.
2. A failed encoder creation leaves the pipeline restartable and surfaces the failure.
3. `start`/`stop` are idempotent; streaming state resets per connection; nothing streams unsolicited.
4. Source timestamps apply both offset and drift, and invert the mapping rather than approximating it.
5. No chunk exceeds 150 ms; backlog beyond a bounded amount is dropped rather than burst.
6. Losing availability sends `client_stream/end` before reporting `available: false`.
7. The trust-level `user` requirement is enforced and pinned by a test.
8. Full suite green on `net10.0`; library builds clean on `net8.0` and `net10.0`; `<Version>9.2.0</Version>` unchanged.
