using System.Net.WebSockets;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A connection the host accepted has to be disposed when it ends, whichever way it ends: the
/// socket, its receive loop and the client's subscription to the shared pipeline are released
/// nowhere else (#344). An admitted connection whose server went away used to keep all three
/// for the life of the process — one more each time a server restarted.
/// </summary>
/// <remarks>
/// Loopback against the host's real listener, like <see cref="SendspinHostServiceSharedPipelineTests"/>,
/// and on the same signal: <see cref="FakeAudioPipeline.SubscriberCount"/> counts the clients
/// that have not been disposed.
/// </remarks>
[Collection("RealSockets")]
public class SendspinHostServiceConnectionDisposalTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] TestPsk = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private static async Task<SendspinHostService> StartHostAsync(
        FakeAudioPipeline pipeline, FakeCaptureDevice capture)
    {
        var records = new InMemoryPairingRecordStore();

        // Unbound LongTerm record — see SendspinHostServiceArbitrationTests for the reasoning.
        records.Upsert(new PairingRecord(TestPsk, PskCategory.LongTerm));

        var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                PairingRecordStore = records,
                AudioPipeline = pipeline,
                CaptureDevice = capture,
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await host.StartAsync();
        return host;
    }

    private static async Task AdmitAsync(SendspinHostService host, FakeServer server)
    {
        var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, ConnectedServerInfo info) => connected.TrySetResult(info.ServerId);

        host.ServerConnected += Handler;
        try
        {
            await server.ConnectAsync(host.ListeningPort);
            Assert.Equal(server.ServerId, await connected.Task.WaitAsync(Timeout));
        }
        finally
        {
            host.ServerConnected -= Handler;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {because}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task AdmittedServerThatCloses_IsDisposed_LeavingTheSharedPipelineAndCaptureDeviceAlone()
    {
        var pipeline = new FakeAudioPipeline();
        var capture = new FakeCaptureDevice();
        await using var host = await StartHostAsync(pipeline, capture);

        // A server restarting, twice: each connection it leaves behind has to be gone before
        // the next one is counted.
        for (int restart = 0; restart < 2; restart++)
        {
            var server = new FakeServer(TestPsk, ["playback"]);
            await AdmitAsync(host, server);
            Assert.Equal(1, pipeline.SubscriberCount);

            await server.DisposeAsync();
            await WaitUntilAsync(() => pipeline.SubscriberCount == 0, "the closed connection to be disposed");
        }

        // The next server to connect uses both. Playback is left as a lost connection always
        // left it: the client does not stop the pipeline when its server goes away.
        Assert.Equal(0, capture.DisposeCount);
        Assert.Equal(0, pipeline.StopCount);
    }

    [Fact]
    public async Task AdmittedServerThatBreaksFraming_AndNeverAnswersTheClose_IsDisposed()
    {
        var pipeline = new FakeAudioPipeline();
        var capture = new FakeCaptureDevice();
        await using var host = await StartHostAsync(pipeline, capture);

        // Nothing but disposal ends this connection's receive loop: the host sends its Close
        // frame and the peer never answers it.
        await using var server = new SilentCloseFakeServer(TestPsk, []);
        await AdmitAsync(host, server);

        await server.SendRawBinaryAsync(new byte[48]);
        await WaitUntilAsync(() => pipeline.SubscriberCount == 0, "the failed connection to be disposed");

        Assert.Empty(host.ConnectedServers);
        Assert.Equal(0, capture.DisposeCount);
    }

    [Fact]
    public async Task AdmittedServerTheClientItselfDrops_IsDisposed()
    {
        var pipeline = new FakeAudioPipeline();
        var capture = new FakeCaptureDevice();
        await using var host = await StartHostAsync(pipeline, capture);
        await using var server = new FakeServer(TestPsk, ["playback"]);
        await AdmitAsync(host, server);

        // The disconnect is raised from inside the client's own message handling here, not by
        // the receive loop ending.
        await server.SendJsonAsync("""{"type":"stream/end","payload":null}""");
        Assert.Equal("unauthorized", await server.WaitForGoodbyeAsync(Timeout));
        await WaitUntilAsync(() => pipeline.SubscriberCount == 0, "the dropped connection to be disposed");

        Assert.Equal(0, capture.DisposeCount);
    }

    [Fact]
    public async Task DisconnectAllAsync_DisposesTheConnectionsItDrops()
    {
        var pipeline = new FakeAudioPipeline();
        var capture = new FakeCaptureDevice();
        await using var host = await StartHostAsync(pipeline, capture);
        await using var server = new FakeServer(TestPsk, ["playback"]);
        await AdmitAsync(host, server);

        await host.DisconnectAllAsync();
        await WaitUntilAsync(() => pipeline.SubscriberCount == 0, "the dropped connection to be disposed");

        // The session the application is leaving for takes both over.
        Assert.Equal(0, capture.DisposeCount);
        Assert.Equal(0, pipeline.StopCount);
    }

    [Fact]
    public async Task StoppingTheHost_DropsAConnectionThatHasNotActivated()
    {
        var pipeline = new FakeAudioPipeline();
        var capture = new FakeCaptureDevice();
        await using var host = await StartHostAsync(pipeline, capture);

        // Provisional: upgraded, and nothing since.
        using var peer = new ClientWebSocket();
        await peer.ConnectAsync(new Uri($"ws://127.0.0.1:{host.ListeningPort}/sendspin"), CancellationToken.None);
        await WaitUntilAsync(() => pipeline.SubscriberCount == 1, "the provisional connection to be built");

        await host.StopAsync();

        Assert.Equal(0, pipeline.SubscriberCount);

        // It never held either.
        Assert.Equal(0, pipeline.StopCount);
        Assert.Equal(0, capture.DisposeCount);
        await AssertClosedByHostAsync(peer);
    }

    [Fact]
    public async Task ConnectionHandedOverAfterTheListenerStopped_IsClosed()
    {
        // The upgrade that completes as the host stops. A second listener stands in for the
        // host's own, which cannot be made to hand one over late on demand.
        await using var listener = new SimpleWebSocketServer();
        listener.Start(0);
        var accepted = new TaskCompletionSource<WebSocketClientConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ClientConnected += (_, connection) => accepted.TrySetResult(connection);

        using var peer = new ClientWebSocket();
        await peer.ConnectAsync(new Uri($"ws://127.0.0.1:{listener.Port}/sendspin"), CancellationToken.None);

        await using var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions { Identity = SendspinIdentity.Generate() },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        var handed = (Task)typeof(SendspinHostService)
            .GetMethod("HandleServerConnectedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(host, [await accepted.Task.WaitAsync(Timeout)])!;
        await handed.WaitAsync(Timeout);

        await AssertClosedByHostAsync(peer);
    }

    /// <summary>The peer's read ends — in a Close frame or a dropped socket — rather than waiting.</summary>
    private static async Task AssertClosedByHostAsync(ClientWebSocket peer)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[4096];
        try
        {
            // Past whatever the host sent first: a provisional connection has its client/init.
            while ((await peer.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token)).MessageType
                != WebSocketMessageType.Close)
            {
            }
        }
        catch (WebSocketException)
        {
            // Dropped without a Close frame.
        }
    }
}
