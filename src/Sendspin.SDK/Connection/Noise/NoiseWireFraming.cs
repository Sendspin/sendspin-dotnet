using System.Text;
using System.Text.Json;
using Noise;
using Sendspin.SDK.Connection.Framing;
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
    private string? _clientInitText;
    private Transport? _transport;
    private byte[]? _handshakeHash;
    private string? _serverId;
    private NoisePsk? _matchedPsk;

    // Fragment reassembly state (one in-flight message per connection, per spec).
    private MemoryStream? _reassemblyBuffer;
    private byte _reassemblyOrigType;

    /// <summary>Creates a client-side Noise framing.</summary>
    /// <param name="identity">The client's static identity (its public key is the client_id).</param>
    /// <param name="pskResolver">Resolves psk_id from Noise message 1 to a PSK candidate.
    /// Defaults to <see cref="SentinelPskResolver"/> (pre-pairing).</param>
    /// <param name="suite">Cipher suite to announce in client/init.</param>
    public NoiseWireFraming(
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
    public NoisePsk? MatchedPsk => _matchedPsk;

    /// <inheritdoc/>
    public IReadOnlyList<WireFrame> Start()
    {
        _clientInitText = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "client/init",
            ["payload"] = new Dictionary<string, object>
            {
                ["client_id"] = _identity.PeerId,
                ["version"] = NoiseConstants.ProtocolVersion,
                ["suite"] = _suite.ToWireName(),
            },
        });
        _phase = HandshakePhase.AwaitingServerInit;
        return [WireFrame.FromText(_clientInitText)];
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
        _phase = HandshakePhase.AwaitingStart;
        _clientInitText = null;
        _handshakeHash = null;
        _serverId = null;
        _matchedPsk = null;
        _reassemblyBuffer?.Dispose();
        _reassemblyBuffer = null;
    }

    // --- Handshake ---

    private InboundFrameResult HandleServerInit(WireFrame frame)
    {
        if (frame.Kind != WireFrameKind.Text)
            return Fail("expected server/init text frame");

        string serverInitText = frame.PayloadAsText();
        using var doc = JsonDocument.Parse(serverInitText);
        if (doc.RootElement.GetProperty("type").GetString() != "server/init")
            return Fail("expected server/init message");

        var payload = doc.RootElement.GetProperty("payload");
        int version = payload.GetProperty("version").GetInt32();
        if (version != NoiseConstants.ProtocolVersion)
            return Fail($"unsupported server version {version}");

        _serverId = payload.GetProperty("server_id").GetString()
            ?? throw new FormatException("server_id missing");

        // Prologue binds the exact wire bytes of both init messages.
        _serverInitText = serverInitText;
        _phase = HandshakePhase.AwaitingNoiseMessage1;
        return InboundFrameResult.None;
    }

    private string? _serverInitText;

    private InboundFrameResult HandleNoiseMessage1(WireFrame frame)
    {
        if (frame.Kind != WireFrameKind.Text)
            return Fail("expected noise/handshake text frame");

        using var doc = JsonDocument.Parse(frame.PayloadAsText());
        if (doc.RootElement.GetProperty("type").GetString() != "noise/handshake")
            return Fail("expected noise/handshake message");
        byte[] msg1 = Base64UrlText.Decode(
            doc.RootElement.GetProperty("payload").GetProperty("data").GetString()!);

        byte[] prologue = Encoding.UTF8.GetBytes(_clientInitText + _serverInitText);
        byte[] serverPub = SendspinIdentity.DecodePeerId(_serverId!);
        var protocol = NoiseProtocol.Parse(_suite.ToProtocolName().AsSpan());

        // Noise.NET consumes PSKs at state creation, but KKpsk2 only needs the PSK when
        // writing message 2 - after message 1 reveals psk_id. Read message 1 with a
        // placeholder PSK to learn psk_id, then rebuild the state with the resolved PSK
        // and replay message 1 (deterministic: the responder adds no randomness before
        // message 2).
        string pskId;
        using (var probeState = protocol.Create(
            initiator: false, prologue: prologue,
            s: _identity.PrivateKey.ToArray(), rs: serverPub,
            psks: [new byte[NoiseConstants.PskSize]]))
        {
            var probeBuf = new byte[NoiseProtocol.MaxMessageLength];
            var (probeLen, _, _) = probeState.ReadMessage(msg1, probeBuf);
            using var payloadDoc = JsonDocument.Parse(Encoding.UTF8.GetString(probeBuf, 0, probeLen));
            pskId = payloadDoc.RootElement.GetProperty("psk_id").GetString()
                ?? throw new FormatException("psk_id missing");
        }

        var resolved = _pskResolver.Resolve(pskId);
        if (resolved is null)
            return Fail($"no PSK matches psk_id {pskId}");
        if (resolved.ServerId is not null && resolved.ServerId != _serverId)
            return Fail("PSK is bound to a different server_id");

        using var state = protocol.Create(
            initiator: false, prologue: prologue,
            s: _identity.PrivateKey.ToArray(), rs: serverPub,
            psks: [resolved.Key.ToArray()]);

        var buf = new byte[NoiseProtocol.MaxMessageLength];
        state.ReadMessage(msg1, buf);
        var (msg2Len, handshakeHash, transport) = state.WriteMessage(Encoding.UTF8.GetBytes("{}"), buf);
        if (transport is null)
            return Fail("handshake did not complete after message 2");

        _transport = transport;
        _handshakeHash = handshakeHash;
        _matchedPsk = resolved;
        _phase = HandshakePhase.TransportMode;

        string reply = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "noise/handshake",
            ["payload"] = new Dictionary<string, object> { ["data"] = Base64UrlText.Encode(buf.AsSpan(0, msg2Len)) },
        });
        return new InboundFrameResult { Replies = [WireFrame.FromText(reply)] };
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
            _reassemblyBuffer = new MemoryStream();
            data = plaintext[2..];
        }
        else
        {
            data = plaintext[1..];
        }

        if (_reassemblyBuffer.Length + data.Length > NoiseConstants.MaxReassembledMessageBytes)
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

    private InboundFrameResult DispatchMessage(byte type, ReadOnlyMemory<byte> payload)
    {
        if (type == NoiseConstants.MessageTypeJsonBody)
            return InboundFrameResult.ForText(Encoding.UTF8.GetString(payload.Span));

        // Non-JSON application binary: surface in the SDK's existing binary message
        // shape ([type][payload]) so BinaryMessageParser sees what it always has.
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
        return InboundFrameResult.Fatal(reason);
    }

    private void ThrowIfNotReady()
    {
        if (_phase != HandshakePhase.TransportMode)
            throw new InvalidOperationException(
                "Noise transport is not ready; application frames may only be sent after the handshake completes");
    }
}
