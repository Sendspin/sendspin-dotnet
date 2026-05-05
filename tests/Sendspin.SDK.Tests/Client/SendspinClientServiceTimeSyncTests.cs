using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

public class SendspinClientServiceTimeSyncTests
{
    [Fact]
    public async Task TimeSyncBurst_WhenAlreadyRunning_SecondCallReturnsImmediately()
    {
        // Regression for the concurrent-burst hazard: the continuous time-sync loop and
        // HandleStreamStart's smart-sync trigger can both invoke SendTimeSyncBurstAsync.
        // The single-slot TCS design can't safely interleave; the _burstRunning guard
        // (Interlocked.CompareExchange) makes the second invocation return immediately.
        var connection = new FakeSendspinConnection();
        await connection.ConnectAsync(new Uri("ws://test"));

        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        using var firstCts = new CancellationTokenSource();
        var firstBurst = client.SendTimeSyncBurstAsync(firstCts.Token);

        // Wait for the first burst to send its first probe and start awaiting a reply.
        // Without a server response, the probe sits in the per-probe timeout.
        await WaitForAsync(() => connection.SentMessages.Count == 1, TimeSpan.FromSeconds(1));

        // Second concurrent call must return immediately and send no message.
        var secondBurst = client.SendTimeSyncBurstAsync(CancellationToken.None);
        await secondBurst.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, connection.SentMessages.Count);

        firstCts.Cancel();
        try { await firstBurst.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task TimeSyncBurst_DiscardsResponseWithMismatchedT1()
    {
        // Unmatched server/time replies (wrong T1, late arrivals, duplicates) must not
        // feed ProcessMeasurement on the synchronizer. The previous implementation had a
        // fallback that called ProcessMeasurement directly on unmatched responses,
        // bypassing the burst-best selection. The new code discards them.
        var connection = new FakeSendspinConnection();
        await connection.ConnectAsync(new Uri("ws://test"));

        var clockSync = new RecordingClockSynchronizer();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            clockSynchronizer: clockSync);

        // Inject a server/time response with a T1 the client never sent.
        const string strayResponse = """
        { "type": "server/time",
          "payload": { "client_transmitted": 999999, "server_received": 1000, "server_transmitted": 1100 } }
        """;
        connection.RaiseTextMessageReceived(strayResponse);

        Assert.Equal(0, clockSync.ProcessMeasurementCallCount);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        if (!condition())
            throw new TimeoutException("Condition not met within timeout");
    }

    private sealed class RecordingClockSynchronizer : Sendspin.SDK.Synchronization.IClockSynchronizer
    {
        public int ProcessMeasurementCallCount { get; private set; }

        public bool IsConverged => false;
        public bool HasMinimalSync => false;
        public double StaticDelayMs { get; set; }

        public void ProcessMeasurement(long t1, long t2, long t3, long t4)
        {
            ProcessMeasurementCallCount++;
        }

        public long ClientToServerTime(long clientTime) => clientTime;
        public long ServerToClientTime(long serverTime) => serverTime;
        public void Reset() { }
        public Sendspin.SDK.Synchronization.ClockSyncStatus GetStatus() => new();
    }
}
