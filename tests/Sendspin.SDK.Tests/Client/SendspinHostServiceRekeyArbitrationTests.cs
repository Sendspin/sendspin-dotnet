using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The holder's arbitration priority between an in-band re-handshake and the server/activate
/// that follows it (#340). connection.md, Re-handshake: "Connection state ... persists across a
/// re-handshake; only the session keys and what the handshake itself derives change", and that
/// activate "is a subsequent one on the same connection" — so the holder ranks as it did before
/// the re-key until it says otherwise. Loopback end-to-end, like
/// <see cref="SendspinHostServiceArbitrationTests"/>.
/// </summary>
/// <remarks>
/// The client notices a re-key on the first message under the new keys, which from a
/// conforming server is the activate itself; the window is then only as wide as the handling of
/// that one message. These tests hold it open with a message the client drops instead.
/// </remarks>
[Collection("RealSockets")]
public class SendspinHostServiceRekeyArbitrationTests
{
    // Same generous ceiling and rationale as SendspinHostServiceArbitrationTests.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] TestPsk = Enumerable.Repeat((byte)0x42, 32).ToArray();
    private static readonly byte[] PairingPsk = Enumerable.Repeat((byte)0x43, 32).ToArray();

    private const string PlaybackActivate =
        """{"type":"server/activate","payload":{"activities":["playback"]}}""";

    // Anything but server/activate is dropped until the activate arrives; it is sent only to
    // make the client notice the re-key.
    private const string DroppedBeforeActivate =
        """{"type":"group/update","payload":{"group_id":"g1","playback_state":"stopped"}}""";

    private static async Task<SendspinHostService> StartHostAsync(CapturingLoggerFactory log)
    {
        var records = new InMemoryPairingRecordStore();

        // Unbound records, as in SendspinHostServiceArbitrationTests.
        records.Upsert(new PairingRecord(TestPsk, PskCategory.LongTerm));
        records.Upsert(new PairingRecord(PairingPsk, PskCategory.Pairing));

        var host = new SendspinHostService(
            log,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                PairingRecordStore = records,
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await host.StartAsync();
        return host;
    }

    private static async Task WaitForServerConnectedAsync(SendspinHostService host, string serverId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? s, ConnectedServerInfo info)
        {
            if (info.ServerId == serverId)
            {
                tcs.TrySetResult();
            }
        }

        host.ServerConnected += Handler;
        try
        {
            if (host.ConnectedServers.Any(c => c.ServerId == serverId))
            {
                tcs.TrySetResult();
            }

            await tcs.Task.WaitAsync(Timeout);
        }
        finally
        {
            host.ServerConnected -= Handler;
        }
    }

    /// <summary>
    /// Re-keys the holder and returns once the host has handled its first message under the new
    /// keys, which is where the client notices the re-key. Sending it is not enough: the
    /// incoming connection could otherwise be arbitrated before the holder has got that far.
    /// </summary>
    private static async Task ReKeyAndHoldBeforeActivateAsync(FakeServer holder, CapturingLoggerFactory log)
    {
        await holder.RehandshakeAsync(TestPsk, Timeout);
        await holder.SendJsonAsync(DroppedBeforeActivate);

        var deadline = DateTime.UtcNow + Timeout;
        while (!log.Messages.Contains("Dropping group/update received before server/activate"))
        {
            Assert.True(DateTime.UtcNow < deadline, "the holder never handled the message under the new keys");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task PairingHolder_ReKeyedOntoItsNewRecord_IsNotDisplacedByIncomingPlayback()
    {
        // The end of every successful pairing: the server re-handshakes onto the record just
        // persisted and only then activates playback. "A pairing attempt is not displaced by an
        // incoming 'playback' or 'pairing' connection."
        var log = new CapturingLoggerFactory();
        await using var host = await StartHostAsync(log);
        await using var holder = new FakeServer(PairingPsk, ["pairing"], pskCategory: "pr");
        await holder.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, holder.ServerId);

        await ReKeyAndHoldBeforeActivateAsync(holder, log);

        await using var incoming = new FakeServer(TestPsk, ["playback"]);
        await incoming.ConnectAsync(host.ListeningPort);

        Assert.Equal("concurrent_attempt", await incoming.WaitForGoodbyeAsync(Timeout));
        Assert.Contains(host.ConnectedServers, c => c.ServerId == holder.ServerId);

        // The promotion then completes on the connection that was kept.
        var lastPlayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.LastPlayedServerIdChanged += (s, id) => lastPlayed.TrySetResult(id);
        await holder.SendJsonAsync(PlaybackActivate);

        Assert.Equal(holder.ServerId, await lastPlayed.Task.WaitAsync(Timeout));
        Assert.Null(await holder.WaitForGoodbyeAsync(TimeSpan.FromMilliseconds(750)));
    }

    [Fact]
    public async Task PlaybackHolder_ReKeyed_StillOutranksIncomingPairing()
    {
        var log = new CapturingLoggerFactory();
        await using var host = await StartHostAsync(log);
        await using var holder = new FakeServer(TestPsk, ["playback"]);
        await holder.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, holder.ServerId);

        await ReKeyAndHoldBeforeActivateAsync(holder, log);

        await using var incoming = new FakeServer(PairingPsk, ["pairing"], pskCategory: "pr");
        await incoming.ConnectAsync(host.ListeningPort);

        Assert.True(await incoming.WaitForPairAbortAsync("concurrent_attempt", Timeout));
        Assert.Null(await holder.WaitForGoodbyeAsync(TimeSpan.FromMilliseconds(750)));
    }
}
