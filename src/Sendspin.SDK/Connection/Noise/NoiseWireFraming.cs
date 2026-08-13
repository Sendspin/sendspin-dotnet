using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noise;
using Sendspin.SDK.Connection.Framing;
using Sendspin.SDK.Protocol;
using NoiseProtocol = Noise.Protocol;

namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// The Sendspin encrypted transport as an <see cref="IWireFraming"/>: owns the
/// cleartext init exchange and Noise KKpsk2 handshake, then encrypts/decrypts all
/// application frames as Noise transport ciphertexts (JSON as binary type 0), splitting
/// and reassembling messages larger than one Noise message via fragment types 2/3.
/// </summary>
/// <remarks>
/// Client-side (Noise responder) only; the server is always the Noise initiator
/// regardless of which side opened the WebSocket. Handshake flow per spec:
/// <c>client/init</c> (via <see cref="Start"/>) → <c>server/init</c> →
/// <c>noise/handshake</c> msg 1 (psk_id inside) → <c>noise/handshake</c> msg 2 (reply)
/// → transport mode. Any protocol/crypto failure surfaces as
/// <see cref="InboundFrameResult.Fatal"/> and the connection closes without an
/// application-level error, per spec.
/// </remarks>
public sealed class NoiseWireFraming : IWireFraming, INoiseSessionInfo
{
    private enum HandshakePhase
    {
        AwaitingStart,
        AwaitingServerInit,
        AwaitingNoiseMessage1,
        TransportMode,
        Failed,
    }

    private readonly SendspinIdentity _identity;
    private readonly INoisePskResolver _pskResolver;
    private readonly NoiseCipherSuite _suite;

    private HandshakePhase _phase = HandshakePhase.AwaitingStart;
    private byte[]? _clientInitBytes;
    private Transport? _transport;
    private byte[]? _handshakeHash;
    private string? _serverId;
    private NoisePsk? _matchedPsk;

    // Deferred re-handshake swap (#81): computed on the receive path, committed on the
    // connection's send path by EncodeDeferredReply. A non-null _pendingTransport marks
    // an uncommitted swap; until the commit, _transport/_handshakeHash/_matchedPsk keep
    // the pre-re-handshake session.
    private Transport? _pendingTransport;
    private byte[]? _pendingHandshakeHash;
    private NoisePsk? _pendingMatchedPsk;
    private string? _pendingReplyJson;

    // Fragment reassembly state (one in-flight message per connection, per spec).
    private MemoryStream? _reassemblyBuffer;
    private byte _reassemblyOrigType;

    // True once a message has genuinely reached the application on this connection;
    // selects between the pre- and post-first-message reassembly bounds.
    private bool _surfacedApplicationMessage;

    /// <summary>Creates a client-side Noise framing.</summary>
    /// <param name="identity">The client's static identity (its public key is the client_id).</param>
    /// <param name="pskResolver">Resolves psk_id from Noise message 1 to a PSK candidate.
    /// Defaults to <see cref="SentinelPskResolver"/> (pre-pairing).</param>
    /// <param name="suite">Cipher suite to announce in client/init.</param>
    internal NoiseWireFraming(
        SendspinIdentity identity,
        INoisePskResolver? pskResolver = null,
        NoiseCipherSuite suite = NoiseCipherSuite.ChaChaPoly)
    {
        _identity = identity;
        _pskResolver = pskResolver ?? SentinelPskResolver.Instance;
        _suite = suite;
    }

    /// <inheritdoc/>
    public bool IsTransportReady => _phase == HandshakePhase.TransportMode;

    /// <summary>The server's id (its static public key) from server/init, once received.</summary>
    public string? ServerId => _serverId;

    /// <summary>The Noise handshake hash <c>h</c>, once the handshake completes.</summary>
    public ReadOnlyMemory<byte>? HandshakeHash => _handshakeHash;

    /// <summary>The PSK that authenticated the current session, once the handshake completes.</summary>
    internal NoisePsk? MatchedPsk => _matchedPsk;

    /// <inheritdoc/>
    NoisePsk? INoiseSessionInfo.MatchedPsk => _matchedPsk;

    /// <inheritdoc/>
    public IReadOnlyList<WireFrame> Start()
    {
        // Fail here, naming the suite and the alternative, rather than several messages later
        // from inside the Noise state machine as an opaque crypto fatal (#89).
        _suite.EnsureSupported();

        // Source-generated rather than reflection: this is the mandatory transport path and
        // reflection-based System.Text.Json breaks it under PublishAot (#89). ClientInitJson's
        // member order reproduces exactly what the dictionary emitted — the prologue binds
        // these literal bytes, so the order is not free to change. Pinned by
        // HandshakeByteFidelityTests.
        string clientInitText = JsonSerializer.Serialize(
            new ClientInitJson(
                "client/init",
                new ClientInitPayloadJson(
                    _identity.PeerId,
                    NoiseConstants.ProtocolVersion,
                    _suite.ToWireName())),
            MessageSerializerContext.Default.ClientInitJson);
        var frame = WireFrame.FromText(clientInitText);
        // Prologue binds the exact wire bytes of both init messages.
        _clientInitBytes = frame.Payload.ToArray();
        _phase = HandshakePhase.AwaitingServerInit;
        return [frame];
    }

    /// <inheritdoc/>
    public IEnumerable<WireFrame> EncodeText(string json)
    {
        ThrowIfNotReady();
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        var plaintext = new byte[1 + utf8.Length];
        plaintext[0] = NoiseConstants.MessageTypeJsonBody;
        utf8.CopyTo(plaintext, 1);
        return EncryptOutbound(plaintext);
    }

    /// <inheritdoc/>
    public IEnumerable<WireFrame> EncodeBinary(ReadOnlyMemory<byte> data)
    {
        ThrowIfNotReady();
        if (data.Length == 0)
            throw new ArgumentException("binary message must include a type byte", nameof(data));
        return EncryptOutbound(data.ToArray());
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Encodes the pending re-handshake reply under the keys being retired and commits
    /// the deferred key swap, in one call, so a transmitted reply can never leave the
    /// swap uncommitted. Must run on the connection's send path under its send lock:
    /// that lock is what serializes this against concurrent application encodes, and
    /// what guarantees the reply reaches the wire before any new-key frame.
    /// </remarks>
    public IReadOnlyList<WireFrame> EncodeDeferredReply()
    {
        if (_pendingTransport is null || _pendingReplyJson is null)
            throw new InvalidOperationException("no deferred re-handshake reply is pending");

        byte[] utf8 = Encoding.UTF8.GetBytes(_pendingReplyJson);
        var plaintext = new byte[1 + utf8.Length];
        plaintext[0] = NoiseConstants.MessageTypeJsonBody;
        utf8.CopyTo(plaintext, 1);

        // Materialize the ciphertext before committing: EncryptOutbound is lazy, and
        // the reply must be encrypted under the OLD transport.
        var frames = EncryptOutbound(plaintext).ToList();

        // Commit: retire the old keys; every encode after this uses the new ones.
        _transport!.Dispose();
        _transport = _pendingTransport;
        _handshakeHash = _pendingHandshakeHash;
        _matchedPsk = _pendingMatchedPsk;
        _pendingTransport = null;
        _pendingHandshakeHash = null;
        _pendingMatchedPsk = null;
        _pendingReplyJson = null;
        return frames;
    }

    /// <inheritdoc/>
    public InboundFrameResult ProcessInbound(WireFrame frame)
    {
        try
        {
            return _phase switch
            {
                HandshakePhase.AwaitingServerInit => HandleServerInit(frame),
                HandshakePhase.AwaitingNoiseMessage1 => HandleNoiseMessage1(frame),
                HandshakePhase.TransportMode => HandleTransportFrame(frame),
                _ => Fail($"frame received in phase {_phase}"),
            };
        }
        catch (Exception ex)
        {
            // Malformed JSON, base64, AEAD failure, Noise library errors: all fatal,
            // close without an application-level error per spec.
            return Fail($"{_phase}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _transport?.Dispose();
        _transport = null;
        ClearPendingSwap();
        _phase = HandshakePhase.AwaitingStart;
        _clientInitBytes = null;
        _serverInitBytes = null;
        _handshakeHash = null;
        _serverId = null;
        _matchedPsk = null;
        _reassemblyBuffer?.Dispose();
        _reassemblyBuffer = null;
        _surfacedApplicationMessage = false;
    }

    // --- Handshake ---

    private InboundFrameResult HandleServerInit(WireFrame frame)
    {
        if (frame.Kind != WireFrameKind.Text)
            return Fail("expected server/init text frame");

        using var doc = JsonDocument.Parse(frame.PayloadAsText());
        if (doc.RootElement.GetProperty("type").GetString() != "server/init")
            return Fail("expected server/init message");

        var payload = doc.RootElement.GetProperty("payload");
        int version = payload.GetProperty("version").GetInt32();
        if (version != NoiseConstants.ProtocolVersion)
            return Fail($"unsupported server version {version}");

        _serverId = payload.GetProperty("server_id").GetString()
            ?? throw new FormatException("server_id missing");

        // Prologue binds the exact wire bytes of both init messages.
        _serverInitBytes = frame.Payload.ToArray();
        _phase = HandshakePhase.AwaitingNoiseMessage1;
        return InboundFrameResult.None;
    }

    private byte[]? _serverInitBytes;

    private InboundFrameResult HandleNoiseMessage1(WireFrame frame)
    {
        if (frame.Kind != WireFrameKind.Text)
            return Fail("expected noise/handshake text frame");

        using var doc = JsonDocument.Parse(frame.PayloadAsText());
        if (doc.RootElement.GetProperty("type").GetString() != "noise/handshake")
            return Fail("expected noise/handshake message");
        byte[] msg1 = Base64UrlText.Decode(
            doc.RootElement.GetProperty("payload").GetProperty("data").GetString()!);

        byte[] prologue = [.. _clientInitBytes!, .. _serverInitBytes!];
        return RunResponderExchange(msg1, prologue);
    }

    /// <summary>
    /// Runs the responder side of one KKpsk2 exchange (initial handshake or in-band
    /// re-handshake): resolves the psk_id from message 1 and completes the handshake.
    /// On the initial handshake the new transport is installed immediately and the
    /// reply returned as a cleartext frame; on a re-handshake the new transport and the
    /// reply are held pending for the connection's send path to encode and commit via
    /// <see cref="EncodeDeferredReply"/> -- nothing is encrypted here, on the receive
    /// path, because a concurrent send may be using the current transport (#81).
    /// </summary>
    private InboundFrameResult RunResponderExchange(byte[] msg1, byte[] prologue)
    {
        byte[] serverPub = SendspinIdentity.DecodePeerId(_serverId!);
        var protocol = NoiseProtocol.Parse(_suite.ToProtocolName().AsSpan());

        // Noise.NET consumes PSKs at state creation, but KKpsk2 only needs the PSK when
        // writing message 2 - after message 1 reveals psk_id. Read message 1 with a
        // placeholder PSK to learn psk_id, then rebuild the state with the resolved PSK
        // and replay message 1 (deterministic: the responder adds no randomness before
        // message 2).
        // Each Create gets its own copy of the private key because Noise.NET takes ownership of
        // the array it is handed. The copies are cleared in a finally rather than left to the
        // library: every path out of this method between the copy and the state's disposal --
        // a malformed message 1, an unresolvable psk_id, a psk_id bound to another server --
        // otherwise leaves a live Curve25519 private key on the heap until GC (#102).
        string pskId;
        byte[] probeKey = _identity.PrivateKey.ToArray();
        try
        {
            using var probeState = protocol.Create(
                initiator: false, prologue: prologue,
                s: probeKey, rs: serverPub,
                psks: [new byte[NoiseConstants.PskSize]]);

            var probeBuf = new byte[NoiseProtocol.MaxMessageLength];
            var (probeLen, _, _) = probeState.ReadMessage(msg1, probeBuf);
            using var payloadDoc = JsonDocument.Parse(Encoding.UTF8.GetString(probeBuf, 0, probeLen));
            pskId = payloadDoc.RootElement.GetProperty("psk_id").GetString()
                ?? throw new FormatException("psk_id missing");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(probeKey);
        }

        var resolved = _pskResolver.Resolve(pskId);
        if (resolved is null)
            return Fail($"no PSK matches psk_id {pskId}");
        if (resolved.ServerId is not null && resolved.ServerId != _serverId)
            return Fail("PSK is bound to a different server_id");

        // Same treatment for the real exchange, and for the resolved PSK's copy alongside it:
        // "handshake did not complete after message 2" returns between the copy and the end of
        // the method.
        byte[] privateKey = _identity.PrivateKey.ToArray();
        byte[] pskCopy = resolved.Key.ToArray();
        try
        {
            return CompleteResponderExchange(
                protocol, prologue, serverPub, privateKey, pskCopy, msg1, resolved);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(pskCopy);
        }
    }

    /// <summary>
    /// The half of <see cref="RunResponderExchange"/> that runs under the resolved PSK, split
    /// out so its caller's <c>finally</c> covers every return path with one block rather than
    /// wrapping the whole method body.
    /// </summary>
    private InboundFrameResult CompleteResponderExchange(
        NoiseProtocol protocol,
        byte[] prologue,
        byte[] serverPub,
        byte[] privateKey,
        byte[] pskCopy,
        byte[] msg1,
        NoisePsk resolved)
    {
        using var state = protocol.Create(
            initiator: false, prologue: prologue,
            s: privateKey, rs: serverPub,
            psks: [pskCopy]);

        var buf = new byte[NoiseProtocol.MaxMessageLength];
        state.ReadMessage(msg1, buf);
        var (msg2Len, handshakeHash, transport) = state.WriteMessage(Encoding.UTF8.GetBytes("{}"), buf);
        if (transport is null)
            return Fail("handshake did not complete after message 2");

        string replyJson = JsonSerializer.Serialize(
            new NoiseHandshakeJson(
                "noise/handshake",
                new NoiseHandshakePayloadJson(Base64UrlText.Encode(buf.AsSpan(0, msg2Len)))),
            MessageSerializerContext.Default.NoiseHandshakeJson);

        if (_transport is null)
        {
            // Initial handshake: install the keys immediately (no application traffic
            // can be in flight yet) and reply as a cleartext text frame.
            _transport = transport;
            _handshakeHash = handshakeHash;
            _matchedPsk = resolved;
            _phase = HandshakePhase.TransportMode;
            return new InboundFrameResult { Replies = [WireFrame.FromText(replyJson)] };
        }

        // Re-handshake: the reply must travel encrypted under the OLD session keys and
        // reach the wire before any frame under the new ones. Both are enforced by the
        // connection's send path: hold everything pending and let EncodeDeferredReply,
        // under the connection's send lock, encode the reply and commit the swap.
        _pendingTransport = transport;
        _pendingHandshakeHash = handshakeHash;
        _pendingMatchedPsk = resolved;
        _pendingReplyJson = replyJson;
        return InboundFrameResult.ForDeferredReply();
    }

    // --- Transport mode ---

    private InboundFrameResult HandleTransportFrame(WireFrame frame)
    {
        if (frame.Kind != WireFrameKind.Binary)
            return Fail("text frame received in transport mode");

        var plainBuf = new byte[frame.Payload.Length];
        int plainLen = _transport!.ReadMessage(frame.Payload.Span, plainBuf);
        if (plainLen == 0)
            return Fail("empty transport message");

        byte type = plainBuf[0];
        return type switch
        {
            NoiseConstants.MessageTypeFragmentMore => HandleFragment(plainBuf.AsMemory(0, plainLen), last: false),
            NoiseConstants.MessageTypeFragmentEnd => HandleFragment(plainBuf.AsMemory(0, plainLen), last: true),
            _ when _reassemblyBuffer is not null =>
                Fail("non-fragment frame received while a fragmented message is in flight"),
            _ => DispatchMessage(type, plainBuf.AsMemory(1, plainLen - 1)),
        };
    }

    private InboundFrameResult HandleFragment(ReadOnlyMemory<byte> plaintext, bool last)
    {
        ReadOnlyMemory<byte> data;
        if (_reassemblyBuffer is null)
        {
            // Opening fragment carries orig_type after the fragment type byte.
            if (last)
                return Fail("fragment-end with no fragmented message in flight");
            if (plaintext.Length < 2)
                return Fail("opening fragment missing orig_type");
            _reassemblyOrigType = plaintext.Span[1];
            if (_reassemblyOrigType is NoiseConstants.MessageTypeFragmentMore or NoiseConstants.MessageTypeFragmentEnd)
                return Fail("orig_type of 2 or 3");
            _reassemblyBuffer = new MemoryStream();
            data = plaintext[2..];
        }
        else
        {
            data = plaintext[1..];
        }

        int maxReassembled = _surfacedApplicationMessage
            ? NoiseConstants.MaxReassembledMessageBytes
            : NoiseConstants.MaxReassembledMessageBytesBeforeFirstMessage;
        if (_reassemblyBuffer.Length + data.Length > maxReassembled)
            return Fail("reassembled message exceeds size bound");
        _reassemblyBuffer.Write(data.Span);

        if (!last)
            return InboundFrameResult.None;

        byte origType = _reassemblyOrigType;
        byte[] assembled = _reassemblyBuffer.ToArray();
        _reassemblyBuffer.Dispose();
        _reassemblyBuffer = null;
        return DispatchMessage(origType, assembled);
    }

    /// <summary>
    /// Handles a server-initiated in-band re-handshake (key rotation / post-pairing
    /// promotion): the new exchange's prologue is the prior handshake's hash, keys and
    /// suite carry over, and the reply travels encrypted under the old session keys.
    /// The framing consumes these messages; they never surface to the application.
    /// </summary>
    private InboundFrameResult HandleRehandshakeMessage(JsonElement root)
    {
        // The server cannot start another exchange before it has received message 2,
        // and message 2 has not been sent while the swap is uncommitted. Failing loudly
        // here also catches a connection that dropped a deferred reply on the floor.
        if (_pendingTransport is not null)
            return Fail("re-handshake message 1 received while a prior key swap is uncommitted");

        byte[] msg1 = Base64UrlText.Decode(
            root.GetProperty("payload").GetProperty("data").GetString()!);
        byte[] prologue = _handshakeHash
            ?? throw new InvalidOperationException("re-handshake before initial handshake");
        return RunResponderExchange(msg1, prologue);
    }

    private InboundFrameResult DispatchMessage(byte type, ReadOnlyMemory<byte> payload)
    {
        if (type == NoiseConstants.MessageTypeJsonBody)
        {
            string json = Encoding.UTF8.GetString(payload.Span);
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("type", out var typeProp)
                    && typeProp.ValueKind == JsonValueKind.String
                    && typeProp.GetString() == "noise/handshake")
                {
                    // Re-handshakes are consumed by the framing and never surface, so
                    // they must not count as the first application message.
                    return HandleRehandshakeMessage(doc.RootElement);
                }
            }
            catch (JsonException)
            {
                // Not JSON at all: an ordinary application message as far as this
                // layer is concerned. Surface it below and let the client's own
                // dispatch judge whether it is well-formed -- this layer does not.
            }

            _surfacedApplicationMessage = true;
            return InboundFrameResult.ForText(json);
        }

        // Non-JSON application binary: surface in the SDK's existing binary message
        // shape ([type][payload]) so BinaryMessageParser sees what it always has.
        _surfacedApplicationMessage = true;
        var full = new byte[1 + payload.Length];
        full[0] = type;
        payload.CopyTo(full.AsMemory(1));
        return InboundFrameResult.ForBinary(full);
    }

    private IEnumerable<WireFrame> EncryptOutbound(ReadOnlyMemory<byte> plaintext)
    {
        if (plaintext.Length <= NoiseConstants.MaxTransportPlaintext)
        {
            yield return EncryptFrame(plaintext.Span);
            yield break;
        }

        // Fragment: [2][orig_type][data...] then [2][data...]* then [3][data...].
        byte origType = plaintext.Span[0];
        ReadOnlyMemory<byte> remaining = plaintext[1..];
        bool first = true;
        while (true)
        {
            int headerLen = first ? 2 : 1;
            int chunkLen = Math.Min(remaining.Length, NoiseConstants.MaxTransportPlaintext - headerLen);
            bool isLast = chunkLen == remaining.Length;

            var fragment = new byte[headerLen + chunkLen];
            fragment[0] = isLast ? NoiseConstants.MessageTypeFragmentEnd : NoiseConstants.MessageTypeFragmentMore;
            if (first)
                fragment[1] = origType;
            remaining[..chunkLen].CopyTo(fragment.AsMemory(headerLen));

            yield return EncryptFrame(fragment);

            if (isLast)
                yield break;
            remaining = remaining[chunkLen..];
            first = false;
        }
    }

    private WireFrame EncryptFrame(ReadOnlySpan<byte> plaintext)
    {
        var ciphertext = new byte[plaintext.Length + 16];
        int written = _transport!.WriteMessage(plaintext, ciphertext);
        return new WireFrame(WireFrameKind.Binary, ciphertext.AsMemory(0, written));
    }

    private InboundFrameResult Fail(string reason)
    {
        _phase = HandshakePhase.Failed;
        _transport?.Dispose();
        _transport = null;
        ClearPendingSwap();
        return InboundFrameResult.Fatal(reason);
    }

    private void ClearPendingSwap()
    {
        _pendingTransport?.Dispose();
        _pendingTransport = null;
        _pendingHandshakeHash = null;
        _pendingMatchedPsk = null;
        _pendingReplyJson = null;
    }

    private void ThrowIfNotReady()
    {
        if (_phase != HandshakePhase.TransportMode)
            throw new InvalidOperationException(
                "Noise transport is not ready; application frames may only be sent after the handshake completes");
    }
}
