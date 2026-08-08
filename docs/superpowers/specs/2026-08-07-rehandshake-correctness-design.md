# Design: re-handshake correctness

*Closes [#80](https://github.com/Sendspin/sendspin-dotnet/issues/80) and [#81](https://github.com/Sendspin/sendspin-dotnet/issues/81). Both are v10 blockers, both live in `NoiseWireFraming`'s in-band re-handshake path — which is how a client reaches `user` trust after pairing, so every successful pairing traverses it.*

## 0. What changed during scoping

Two of the three defects are worse than their issues describe. Recorded here because the severity changes the fix, not just the urgency.

**#80's substring sniff can kill the connection, not merely misroute.** `HandleRehandshakeMessage` re-parses and does `doc.RootElement.GetProperty("type")`, which throws `KeyNotFoundException` when the document has no root `type` member (and `InvalidOperationException` when the root is not an object). An application message whose *content* contains the literal `"noise/handshake"` — an artwork URL, a track title, a management record field — and which lacks a root `type` therefore throws into `ProcessInbound`'s envelope, which converts everything to a `Fatal`. The connection closes.

**#81 is an AEAD nonce-reuse hazard, not only a message-ordering one.** The issue describes frames straddling the swap and replies arriving out of order. The sharper problem is in `RunResponderExchange`:

```csharp
var previousTransport = _transport;
_transport = transport;              // swap, on the receive thread
…
// reply encrypted here, under previousTransport, still on the receive thread
```

`_sendLock` lives on the connection, and the receive path never takes it. So a send thread that read `_transport` just before the swap holds the **same object** the receive thread then encrypts the reply with. Two concurrent `WriteMessage` calls on one transport race its nonce counter, and AEAD nonce reuse is a confidentiality break, not a glitch.

`IWireFraming`'s contract says implementations "need not be thread-safe: connections serialize encode calls under their send lock and process inbound frames on a single receive path." That claim is **false for the re-handshake case** — the receive path performs an encrypt. It reads as an invariant while being an unchecked assumption, which is why this survived review.

## 1. #80.1 — dispatch on the parsed type

The framing already parses inbound JSON to decide what to do; the sniff exists only to avoid parsing twice. Replace it: parse once in `DispatchMessage`, read the root `type`, and route on its value. `noise/handshake` goes to the re-handshake handler; everything else surfaces.

Parsing must not throw on application content. A frame that is not JSON, or whose root is not an object, or which has no `type`, is a perfectly ordinary application message as far as the framing is concerned — the framing is not the layer that decides whether an application message is well-formed. It surfaces it and lets the client's own dispatch apply its rules ([#113](https://github.com/Sendspin/sendspin-dotnet/pull/113) made that layer close the connection on genuinely unparseable frames, which is the right place for it).

This removes the false-positive path entirely rather than handling it, so `_surfacedApplicationMessage` bookkeeping stops depending on a sniff outcome.

## 2. #80.2 — prologue from the received bytes

`_serverInitText` is a `string` obtained from `WireFrame.PayloadAsText()`, and the prologue is `Encoding.UTF8.GetBytes(_clientInitText + _serverInitText)`. The spec requires the exact wire bytes of `client/init` ‖ `server/init`.

A UTF-8 round trip is lossless for valid UTF-8, so this is narrow — it diverges only for input that is not valid UTF-8 (lone surrogates become U+FFFD) or that carries a BOM. But the prologue is a security input to the Noise handshake: if the two sides compute it differently the handshake fails, and if an attacker can influence the difference that is worth caring about.

Retain the received payload bytes of `server/init` and concatenate those with the bytes we sent for `client/init`. Our own `client/init` text is ours and was produced by our serializer, so its byte form is already canonical — but store both as bytes so the prologue is a byte-level concatenation end to end, and the invariant is visible rather than argued.

## 3. #81 — the key swap and the reply must both be under the send lock

Two properties must hold, and only one is about ordering:

1. **No two threads may use one transport concurrently.** This is the nonce-reuse hazard.
2. **The reply, encrypted under the old keys, must reach the wire before any frame encrypted under the new keys.** Spec [#122](https://github.com/Sendspin/spec/pull/122): *"Noise message 2 is still encrypted under the pre-re-handshake transport keys; the first frame each side sends after the handshake completes uses the new keys."*

### The chosen shape: defer the swap, encode the reply through the normal send path

`RunResponderExchange` stops encrypting and stops swapping. It computes message 2 and the new transport, stores them as **pending**, and returns the reply as *plaintext* for the connection to send.

The connection then, on its send path and under its own `_sendLock`:

1. encodes the plaintext reply through the ordinary outbound path — which still holds the **old** transport, because the swap has not happened;
2. sends it;
3. calls a framing method that commits the pending swap.

All three steps happen under one `_sendLock` acquisition. That gives both properties without introducing a second lock:

- the receive thread never encrypts, so no transport is ever used by two threads;
- an application send either precedes the whole sequence (old keys, ordered before the reply — fine, it was sent before the re-handshake began) or blocks on `_sendLock` and observes the committed swap (new keys, after the reply).

### Why not simply take a lock around the swap

Taking the connection's `_sendLock` from inside the framing inverts the layering — the framing does not own that lock, and the contract deliberately says it need not be thread-safe. Adding a framing-internal lock would not help either: the send path's `EncryptFrame` and the receive path's reply encryption would still be two different critical sections unless both took the *same* lock, which is the connection's.

Deferring the swap keeps the existing division of responsibility: the framing stays single-threaded-by-contract, and the connection — which already owns send serialization — is the only thing that orders anything.

### Replies stay fire-and-forget

`IncomingConnection` dispatches replies as `_ = SendRepliesSafeAsync(replies)` because the socket callbacks are synchronous and blocking the receive path would deadlock. That stays. Ordering does not require the receive path to block: it requires the *send* path to be serialized, and `_sendLock` already does that. A concurrent application send simply queues behind the reply-and-commit sequence.

### The initial handshake is unaffected

On the initial handshake `previousTransport` is null and the reply travels as a cleartext text frame with no swap to defer. That path keeps its current shape; only the re-handshake branch changes.

## 4. Out of scope

| Deferred | Reason |
|---|---|
| The `IWireFraming` contract wording | It becomes true again once the receive path stops encrypting. Worth a doc pass, but no behavioural change; fold into [#96](https://github.com/Sendspin/sendspin-dotnet/issues/96) |
| [#118](https://github.com/Sendspin/sendspin-dotnet/issues/118) pairing quiescence | Different window, different mechanism |
| Zeroizing the retired transport ([#102](https://github.com/Sendspin/sendspin-dotnet/issues/102)) | The swap is the natural place to do it, but key zeroization is its own slice |

Package version stays `9.1.0`; [#91](https://github.com/Sendspin/sendspin-dotnet/issues/91) owns the bump.

## 5. Testing

The standing bar applies: any test asserting something did *not* happen must survive "would this pass if the machinery producing it were deleted?" That check has found a real gap in every slice on this branch.

| Item | Test | The assertion that matters |
|---|---|---|
| §1 | an application text frame whose **content** contains `"noise/handshake"` and which has **no root `type`** | it **surfaces** as an application message and the connection stays up. Against current code this throws and closes the connection — so it is a genuine regression test, not a tautology |
| §1 | an application frame whose root `type` is some other value but whose body contains the literal | surfaces; is not routed to the re-handshake handler |
| §1 | a real `noise/handshake` frame | still routes to the re-handshake handler — the positive control, without which "never route" would pass |
| §2 | `server/init` containing a multi-byte UTF-8 sequence | the prologue equals the byte concatenation of the two payloads, compared against bytes captured from the wire rather than recomputed from strings |
| §3 | a re-handshake concurrent with application sends | **no frame is encrypted under a mixed or wrong key**, and the reply precedes every new-key frame on the wire. This is the test #81 says does not exist |
| §3 | the swap is not observable before the reply is sent | an application send racing the re-handshake either lands entirely before the reply or entirely after — never between the swap and the reply |
| §3 | initial handshake | unchanged: cleartext reply, no deferred swap. Regression guard on the branch this design does not touch |

The §3 concurrency tests need a deterministic gate rather than timing — a send held open at a controllable point, in the manner of the `HoldNextSend` fake already used elsewhere in the suite — so they fail reliably rather than occasionally.

## 6. Success criteria

1. No substring sniff remains; routing is by parsed `type`, and no application content can reach the re-handshake handler.
2. An application frame lacking a root `type` cannot close the connection from the framing layer.
3. The prologue is a byte-level concatenation of the two `init` payloads as received and sent.
4. The receive path never encrypts, so no transport is ever used concurrently by two threads.
5. The re-handshake reply reaches the wire before any frame encrypted under the new keys, and the swap is committed under the same `_sendLock` acquisition that sent it.
6. The initial-handshake path is behaviourally unchanged.
7. Full suite green on `net10.0`; library builds clean on `net8.0` and `net10.0`; `<Version>9.1.0</Version>` unchanged.
