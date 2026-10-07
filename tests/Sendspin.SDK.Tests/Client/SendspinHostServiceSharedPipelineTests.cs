using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Every connection a host accepts is built over the same audio pipeline and capture device,
/// and only the admitted one is using them. Disposing any other must leave both alone (#311):
/// a peer with no credential could otherwise stop playback, and close the capture device for
/// good, by completing a WebSocket upgrade and hanging up.
/// </summary>
/// <remarks>
/// Loopback against the host's real listener, like <see cref="SendspinHostServiceArbitrationTests"/>.
/// <see cref="FakeAudioPipeline.SubscriberCount"/> is the signal that a client was disposed: a
/// client unsubscribes from the pipeline nowhere else.
/// </remarks>
[Collection("RealSockets")]
public class SendspinHostServiceSharedPipelineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] TestPsk = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private static async Task<SendspinHostService> StartHostAsync(
        FakeAudioPipeline pipeline, FakeCaptureDevice capture)
    {
        var records = new InMemoryPairingRecordStore();

        // Unbound LongTerm record, so a playback activate is admissible — see
        // SendspinHostServiceArbitrationTests for the full reasoning.
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

    private static async Task<FakeServer> AdmitAsync(
        SendspinHostService host, params string[] activities)
    {
        var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, ConnectedServerInfo info) => connected.TrySetResult(info.ServerId);

        var server = new FakeServer(TestPsk, activities);
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

        return server;
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
    public async Task PeerThatHangsUpBeforeActivating_LeavesTheSharedPipelineAndCaptureDeviceAlone()
    {
        var pipeline = new FakeAudioPipeline();
        var capture = new FakeCaptureDevice();
        await using var host = await StartHostAsync(pipeline, capture);
        await using var admitted = await AdmitAsync(host, "playback");

        // No Noise handshake, no credential: the upgrade alone gets a connection built over the
        // shared pipeline, and hanging up gets it disposed.
        using var peer = new ClientWebSocket();
        await peer.ConnectAsync(new Uri($"ws://127.0.0.1:{host.ListeningPort}/sendspin"), CancellationToken.None);
        await WaitUntilAsync(() => pipeline.SubscriberCount == 2, "the unadmitted connection to be built");
        peer.Abort();
        await WaitUntilAsync(() => pipeline.SubscriberCount == 1, "the unadmitted connection to be disposed");

        Assert.Equal(0, pipeline.StopCount);
        Assert.Equal(0, capture.DisposeCount);
    }

    [Fact]
    public async Task ServerThatLosesArbitration_LeavesTheSharedPipelineAndCaptureDeviceAlone()
    {
        var pipeline = new FakeAudioPipeline();
        var capture = new FakeCaptureDevice();
        await using var host = await StartHostAsync(pipeline, capture);
        await using var admitted = await AdmitAsync(host, "playback");

        await using var incoming = new FakeServer(TestPsk, []);
        await incoming.ConnectAsync(host.ListeningPort);
        Assert.Equal("concurrent_attempt", await incoming.WaitForGoodbyeAsync(Timeout));
        await WaitUntilAsync(() => pipeline.SubscriberCount == 1, "the rejected connection to be disposed");

        Assert.Equal(0, pipeline.StopCount);
        Assert.Equal(0, capture.DisposeCount);
    }

    [Fact]
    public async Task DisplacedHolder_IsDisposed_WithoutStoppingTheSharedPipeline()
    {
        var pipeline = new FakeAudioPipeline();
        var capture = new FakeCaptureDevice();
        await using var host = await StartHostAsync(pipeline, capture);
        await using var displaced = await AdmitAsync(host);

        await using var winner = await AdmitAsync(host, "playback");
        Assert.Equal("another_server", await displaced.WaitForGoodbyeAsync(Timeout));

        // The evicted client used to stay subscribed to the shared pipeline for the life of the
        // process, one more per eviction.
        await WaitUntilAsync(() => pipeline.SubscriberCount == 1, "the displaced client to be disposed");

        // The pipeline is the winner's by now.
        Assert.Equal(0, pipeline.StopCount);
        Assert.Equal(0, capture.DisposeCount);
    }

    [Fact]
    public async Task StoppingTheHost_StillStopsTheAdmittedConnectionsPlayback()
    {
        var pipeline = new FakeAudioPipeline();
        var capture = new FakeCaptureDevice();
        await using var host = await StartHostAsync(pipeline, capture);
        await using var admitted = await AdmitAsync(host, "playback");

        await host.StopAsync();

        Assert.Equal(1, pipeline.StopCount);
        Assert.Equal(0, pipeline.SubscriberCount);
    }

    [Fact]
    public async Task DisposingADialledClient_StillStopsItsPipelineAndDisposesItsCaptureDevice()
    {
        var pipeline = new FakeAudioPipeline();
        var capture = new FakeCaptureDevice();
        var (client, _, _) = TestClient.Create(
            configure: o => o with { AudioPipeline = pipeline, CaptureDevice = capture });

        await client.DisposeAsync();

        Assert.Equal(1, pipeline.StopCount);
        Assert.Equal(1, capture.DisposeCount);
    }
}
