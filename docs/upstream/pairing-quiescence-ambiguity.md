# Note for Sendspin maintainers: is the pairing exchange exclusive?

**Prepared 2026-08-13 against spec `3f8528a9` and `aiosendspin` 9.1.0. Not filed — for the maintainers to route.**

## The short version

The spec and the reference server disagree about whether a client may send `client/time` while a
pairing attempt is in progress. They cannot both be right, and the disagreement is not
implementation-specific: **any conformant client that keeps its clock synchronised during a
pairing window will fail to pair against `aiosendspin`.**

## What the spec says

`messaging.md` (single-page `README.md` §"Server → Client: `server/activate`") says that after the
initial `server/activate` the client may send

> any other messages (including `client/time` and the initial `client/state` message **if the
> client has roles that require state updates**)

`client/state` carries a qualifier. `client/time` carries none. A literal reading therefore permits
`client/time` during a pairing activation.

Nothing in `pairing.md` states that the exchange is exclusive. The sequence diagrams show only
pairing messages, but a diagram showing the happy path is not normally read as forbidding
concurrent traffic.

## What `aiosendspin` does

`_receive_pairing` treats **any** non-pairing frame as a protocol error: it closes the WebSocket
with no application-level message, per the spec's Protocol Errors rule.

So a client following the literal reading of `messaging.md` — maintaining clock sync, as a player
must — sends a `client/time` into the pairing window and has its connection closed mid-attempt,
with no diagnostic on either side.

## Why it bites in practice rather than in theory

The window is not short. `dynamic_pin` and `static_pin` both wait on a human: the operator reads a
PIN off a display or off the device, then types it into the server. The spec's own recommended
attempt timeout is two minutes. A player's time-sync loop runs continuously, so over a
human-length window a probe is close to certain.

The server compounds it by not reading the socket while it waits on the operator, so the probe sits
buffered and is then interpreted as the next pairing message.

## Our position, for what it is worth

We have implemented the **stricter** reading — between a pairing `server/activate` and the first
non-pairing one, our client sends nothing but pairing messages. That interoperates with both
readings, so we are not blocked and are not asking for anything urgently.

We are raising it because the ambiguity will catch every other client implementation the same way,
and 1.0 seems like the moment to settle it.

## What would resolve it

Either would work for us; the first is what we assumed:

1. **State that the pairing exchange is exclusive.** Add to `pairing.md` that between a pairing
   `server/activate` and the activation that ends it, a client MUST send only pairing messages,
   and amend the `server/activate` sentence in `messaging.md` so `client/time` is not listed
   without qualification. This matches what `aiosendspin` already enforces, so no server changes
   are needed.

2. **Have the server tolerate interleaved non-pairing frames.** Relax `_receive_pairing` to skip
   frames that are not part of the exchange rather than treating them as protocol errors. This
   matches the current literal text of `messaging.md`, and costs no client changes.

A third option worth naming only to reject it: leaving it as is and letting each client discover
the behaviour against a live server. That is what we did, and the symptom — pairing that fails
partway with no error on either side — took a while to attribute.

## Related, and lower priority

While enumerating what can speak during the window we noticed a second server-shape dependency: a
pairing `server/activate` that **omits** `active_roles` leaves the client's previously granted
roles standing, so a streaming `source@v1` client keeps sending audio frames into the window.
`aiosendspin` sends `active_roles: []`, which stops it — but the spec says `active_roles` is
"required on the first `server/activate`; persists across subsequent messages that omit it", so a
server that omits it on the pairing activate is conformant and would break the attempt.

If the exclusivity rule is added, it covers this case too, since the client would gate its own
sends regardless of what the roles say. Worth a sentence either way.
