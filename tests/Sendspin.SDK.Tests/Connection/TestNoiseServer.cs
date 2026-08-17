using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Noise;
using Sendspin.SDK.Connection.Noise;
using NoiseProtocol = Noise.Protocol;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>Server-side Noise initiator, mirroring aiosendspin's server role.</summary>
internal sealed class TestNoiseServer
{
    /// <summary>
    /// Static key pair. <b>Always pass <c>(byte[])_keys.PrivateKey.Clone()</c> to
    /// <c>Protocol.Create</c>, never <c>_keys.PrivateKey</c> itself.</b>
    /// </summary>
    /// <remarks>
    /// Noise.NET <b>zeroes the private key it is handed</b> when the handshake state is
    /// disposed. The clone is therefore the only reason one key pair can drive more than one
    /// handshake — which is what a reconnect against the same server id depends on
    /// (<c>SameServerReconnect_SendsUserRequestToStaleConnection</c>), and what the three
    /// <c>Protocol.Create</c> calls below rely on. It reads like a redundant defensive cast a
    /// cleanup pass would delete; deleting it breaks that test as an apparent arbitration bug,
    /// a long way from the cause (#99).
    /// </remarks>
    private readonly KeyPair _keys;
    private readonly ReadOnlyMemory<byte> _clientPublicKey;
    private readonly byte[] _psk;
    private readonly string _protocolName;
    private HandshakeState? _state;
    private Transport? _transport;

    /// <param name="clientPublicKey">The client's raw static public key (its client_id, decoded).</param>
    /// <param name="psk">The PSK this session authenticates with.</param>
    /// <param name="keys">Static key pair; pass an existing pair to reuse one server_id across instances.</param>
    /// <param name="suite">Cipher suite to run; must match what the client announced in client/init.</param>
    internal TestNoiseServer(
        ReadOnlyMemory<byte> clientPublicKey,
        byte[] psk,
        KeyPair? keys = null,
        NoiseCipherSuite suite = NoiseCipherSuite.ChaChaPoly)
    {
        _keys = keys ?? KeyPair.Generate();
        _clientPublicKey = clientPublicKey;
        _psk = psk;
        _protocolName = suite.ToProtocolName();
        ServerId = TestBase64Url.EncodeToString(_keys.PublicKey);
    }

    internal string ServerId { get; }

    internal byte[]? HandshakeHash { get; private set; }

    internal (string ServerInitText, string Msg1Text) Respond(string clientInitText)
    {
        string serverInitText = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "server/init",
            ["payload"] = new Dictionary<string, object> { ["server_id"] = ServerId, ["version"] = 1 },
        });

        byte[] prologue = Encoding.UTF8.GetBytes(clientInitText + serverInitText);
        var protocol = NoiseProtocol.Parse(_protocolName.AsSpan());
        _state = protocol.Create(
            initiator: true, prologue: prologue,
            s: (byte[])_keys.PrivateKey.Clone(), // Load-bearing clone — see _keys.
            rs: _clientPublicKey.ToArray(),
            psks: [_psk]);

        string msg1Payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["psk_id"] = NoiseConstants.DerivePskId(_psk),
        });
        var buf = new byte[NoiseProtocol.MaxMessageLength];
        var (len, _, _) = _state.WriteMessage(Encoding.UTF8.GetBytes(msg1Payload), buf);

        string msg1Text = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "noise/handshake",
            ["payload"] = new Dictionary<string, object> { ["data"] = TestBase64Url.EncodeToString(buf.AsSpan(0, len)) },
        });
        return (serverInitText, msg1Text);
    }

    internal void CompleteHandshake(string msg2Text)
    {
        using var doc = JsonDocument.Parse(msg2Text);
        byte[] msg2 = TestBase64Url.DecodeFromChars(
            doc.RootElement.GetProperty("payload").GetProperty("data").GetString()!);
        var buf = new byte[NoiseProtocol.MaxMessageLength];
        var (_, hash, transport) = _state!.ReadMessage(msg2, buf);
        _transport = transport ?? throw new InvalidOperationException("handshake incomplete");
        HandshakeHash = hash;
        _state.Dispose();
    }

    /// <summary>
    /// Like <see cref="Respond"/>, but drives the responder handshake with a caller-supplied
    /// prologue instead of recomputing one from the init texts. Lets a test prove the client
    /// computed the identical prologue by completing a real handshake against a prologue built
    /// from bytes the test captured off the wire, rather than by re-deriving the expected value
    /// from strings and comparing that -- the failure mode this exists to catch.
    /// </summary>
    internal string RespondWithPrologue(byte[] prologue)
    {
        var protocol = NoiseProtocol.Parse(_protocolName.AsSpan());
        _state = protocol.Create(
            initiator: true, prologue: prologue,
            s: (byte[])_keys.PrivateKey.Clone(), // Load-bearing clone — see _keys.
            rs: _clientPublicKey.ToArray(),
            psks: [_psk]);

        string msg1Payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["psk_id"] = NoiseConstants.DerivePskId(_psk),
        });
        var buf = new byte[NoiseProtocol.MaxMessageLength];
        var (len, _, _) = _state.WriteMessage(Encoding.UTF8.GetBytes(msg1Payload), buf);

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "noise/handshake",
            ["payload"] = new Dictionary<string, object> { ["data"] = TestBase64Url.EncodeToString(buf.AsSpan(0, len)) },
        });
    }

    /// <summary>Initiates an in-band re-handshake to a new PSK; returns the encrypted msg1 frame.</summary>
    internal byte[] StartRehandshake(byte[] newPsk)
    {
        var protocol = NoiseProtocol.Parse(_protocolName.AsSpan());
        _state = protocol.Create(
            initiator: true, prologue: HandshakeHash!,
            s: (byte[])_keys.PrivateKey.Clone(), // Load-bearing clone — see _keys.
            rs: _clientPublicKey.ToArray(),
            psks: [newPsk]);
        string payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["psk_id"] = NoiseConstants.DerivePskId(newPsk),
        });
        var buf = new byte[NoiseProtocol.MaxMessageLength];
        var (len, _, _) = _state.WriteMessage(Encoding.UTF8.GetBytes(payload), buf);
        string msg1Text = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "noise/handshake",
            ["payload"] = new Dictionary<string, object> { ["data"] = TestBase64Url.EncodeToString(buf.AsSpan(0, len)) },
        });
        byte[] plain = [0, .. Encoding.UTF8.GetBytes(msg1Text)];
        return EncryptFrame(plain);
    }

    /// <summary>Completes the re-handshake from the client's encrypted msg2 reply (old keys).</summary>
    internal void CompleteRehandshake(byte[] encryptedReply)
    {
        byte[] plain = DecryptFrame(encryptedReply);
        Assert.Equal(0, plain[0]);
        CompleteHandshake(Encoding.UTF8.GetString(plain[1..]));
    }

    internal byte[] EncryptFrame(byte[] plaintext)
    {
        var buf = new byte[plaintext.Length + 16];
        int written = _transport!.WriteMessage(plaintext, buf);
        return buf[..written];
    }

    internal byte[] DecryptFrame(byte[] ciphertext)
    {
        var buf = new byte[ciphertext.Length];
        int len = _transport!.ReadMessage(ciphertext, buf);
        return buf[..len];
    }

    internal IEnumerable<byte[]> EncryptFragmented(byte[] appMessage)
    {
        byte origType = appMessage[0];
        ReadOnlyMemory<byte> remaining = appMessage.AsMemory(1);
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

    internal byte[] DecryptAndReassemble(IEnumerable<byte[]> frames)
    {
        using var assembled = new MemoryStream();
        byte? origType = null;
        foreach (var frame in frames)
        {
            byte[] plaintext = DecryptFrame(frame);
            if (origType is null && plaintext[0] is not (NoiseConstants.MessageTypeFragmentMore or NoiseConstants.MessageTypeFragmentEnd))
            {
                return plaintext;
            }

            if (origType is null)
            {
                origType = plaintext[1];
                assembled.Write(plaintext.AsSpan(2));
            }
            else
            {
                assembled.Write(plaintext.AsSpan(1));
            }
        }

        return [origType!.Value, .. assembled.ToArray()];
    }
}
