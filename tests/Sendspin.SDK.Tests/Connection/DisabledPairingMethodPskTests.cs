using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Framing;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// connection.md: "A PSK for a pairing method disabled in the client's pairing config is
/// excluded from the candidate set, so a handshake referencing it is treated as a lookup miss."
/// The resolver used to hand back any matching record, leaving a server with a stale or
/// leaked Pairing PSK holding an authenticated channel that was only refused later, if it
/// went on to attempt a pairing activation (#202).
/// </summary>
/// <remarks>
/// A lookup miss in the initial handshake is answered with the Sentinel PSK (connection.md
/// § Sentinel Fallback), so the observable outcome of the exclusion is a Sentinel-keyed
/// session at trust 'none' rather than a failed handshake. Either way the disabled record
/// never authenticates the channel, which is the property #202 is about.
/// </remarks>
public class DisabledPairingMethodPskTests
{
    [Fact]
    public void DisabledPairingPsk_IsExcludedFromTheCandidateSet()
    {
        byte[] psk = Psk(0x11);
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(psk, PskCategory.Pairing));
        var resolver = new RecordPskResolver(store, () => false);

        var result = Handshake(SendspinIdentity.Generate(), resolver, psk, out var framing);

        // A lookup miss, not a late refusal: the excluded record never keys the session, so
        // there is no Pairing-trust channel to refuse a pairing activation on later.
        Assert.Null(result.FatalReason);
        Assert.Equal(PskCategory.Sentinel, framing.MatchedPsk!.Category);
    }

    [Fact]
    public void PairingPskEnablement_IsReadAtResolveTime()
    {
        // management/set-pairing-config can flip the method mid-session, so a snapshot taken
        // when the resolver was built is the wrong answer for every handshake after the
        // change. One resolver, two handshakes, opposite outcomes.
        byte[] psk = Psk(0x22);
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(psk, PskCategory.Pairing));
        bool enabled = false;
        var resolver = new RecordPskResolver(store, () => enabled);
        var identity = SendspinIdentity.Generate();

        Handshake(identity, resolver, psk, out var disabled);
        Assert.Equal(PskCategory.Sentinel, disabled.MatchedPsk!.Category);

        enabled = true;
        var result = Handshake(identity, resolver, psk, out var framing);

        Assert.Null(result.FatalReason);
        Assert.Equal(PskCategory.Pairing, framing.MatchedPsk!.Category);
    }

    [Fact]
    public void DisabledPairingPsk_DoesNotExcludeLongTermRecords()
    {
        // Only the pairing_psk method's own bootstrap secret is disabled. A long-term record
        // is what a completed pairing produced, not a pairing-method PSK, so an already-paired
        // server must keep authenticating however the method is configured -- a filter that
        // took the whole store out would lock every paired client out of its own servers.
        byte[] longTerm = Psk(0x33);
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(longTerm, PskCategory.LongTerm));
        store.Upsert(new PairingRecord(Psk(0x44), PskCategory.Pairing));
        var resolver = new RecordPskResolver(store, () => false);

        var result = Handshake(SendspinIdentity.Generate(), resolver, longTerm, out var framing);

        Assert.Null(result.FatalReason);
        Assert.Equal(PskCategory.LongTerm, framing.MatchedPsk!.Category);
    }

    [Fact]
    public void UnfilteredResolver_StillResolvesPairingRecords()
    {
        // Positive control for the default: callers that supply no pairing config (every
        // existing construction outside the two client entry points) are unchanged.
        byte[] psk = Psk(0x55);
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(psk, PskCategory.Pairing));

        var resolved = new RecordPskResolver(store).Resolve(NoiseConstants.DerivePskId(psk));

        Assert.Equal(PskCategory.Pairing, resolved!.Category);
    }

    [Fact]
    public async Task CreateForDial_WiresTheClientsPairingConfigIntoTheResolver()
    {
        // The filter is only worth anything if the client's live config actually reaches the
        // resolver, and that wiring lives at the construction site rather than in the resolver.
        // Drive the handshake through the framing the factory built, bypassing the connection.
        byte[] psk = Psk(0x66);
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(psk, PskCategory.Pairing));
        var identity = SendspinIdentity.Generate();

        var client = SendspinClientService.CreateForDial(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = identity,
                Suite = NoiseCipherSuite.ChaChaPoly,
                PairingRecordStore = store,
                Capabilities = new ClientCapabilities { PairingPskEnabled = false },
            });

        await using (client)
        {
            var framing = (NoiseWireFraming)client.Session;
            var server = new TestNoiseServer(identity.PublicKey, psk);
            var clientInit = Assert.Single(framing.Start());
            var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
            framing.ProcessInbound(WireFrame.FromText(serverInit));

            var result = framing.ProcessInbound(WireFrame.FromText(msg1));

            Assert.Null(result.FatalReason);
            Assert.Equal(PskCategory.Sentinel, framing.MatchedPsk!.Category);
        }
    }

    private static byte[] Psk(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    /// <summary>
    /// Drives the responder side of a real handshake against a server initiating with
    /// <paramref name="serverPsk"/>, and returns the result of processing message 1 — the
    /// message whose psk_id the resolver is asked about.
    /// </summary>
    private static InboundFrameResult Handshake(
        SendspinIdentity identity, INoisePskResolver resolver, byte[] serverPsk, out NoiseWireFraming framing)
    {
        framing = new NoiseWireFraming(identity, resolver);
        var server = new TestNoiseServer(identity.PublicKey, serverPsk);
        var clientInit = Assert.Single(framing.Start());
        var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
        framing.ProcessInbound(WireFrame.FromText(serverInit));
        return framing.ProcessInbound(WireFrame.FromText(msg1));
    }
}
