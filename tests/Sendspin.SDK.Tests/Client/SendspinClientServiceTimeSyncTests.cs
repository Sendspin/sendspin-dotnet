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
        var (client, connection, _) = TestClient.Create();
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));

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
        var clockSync = new RecordingClockSynchronizer();
        var (client, connection, _) = TestClient.Create(configure: options => options with { ClockSynchronizer = clockSync });
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));

        // Inject a server/time response with a T1 the client never sent.
        const string strayResponse = """
        { "type": "server/time",
          "payload": { "client_transmitted": 999999, "server_received": 1000, "server_transmitted": 1100 } }
        """;
        connection.RaiseTextMessageReceived(strayResponse);

        Assert.Equal(0, clockSync.ProcessMeasurementCallCount);
    }

    [Fact]
    public async Task TimeSyncBurst_PropagatesSendFailureOutsideTheTransportSet()
    {
        // #109: the burst's catch was a catch-all, so any failure in the probe path was logged
        // once per burst and the loop simply tried again on the next interval — forever, with
        // the client never converging. The filter now names the transport failures worth
        // retrying; a type outside that set is a bug in our own send path and propagates to
        // TimeSyncLoopAsync's guard, which ends the loop and reports it once.
        //
        // NotSupportedException stands in for that class of fault: nothing in SendSingleProbeAsync
        // raises it legitimately, which is the point — the assertion is about the filter's shape,
        // not this particular type.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));

        connection.NextSendFailure = new NotSupportedException("bug in the probe send path");
        connection.ThrowOnNextSend = true;

        await Assert.ThrowsAsync<NotSupportedException>(
            () => client.SendTimeSyncBurstAsync(CancellationToken.None));
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
