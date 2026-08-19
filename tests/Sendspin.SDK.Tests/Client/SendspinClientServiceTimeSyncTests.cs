using System.Diagnostics;
using Sendspin.SDK.Protocol.Messages;

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

    [Fact]
    public async Task TimeSyncBurst_UsesTheTransportsT1_NotOneStampedBeforeTheSend()
    {
        // #227: the probe's client_transmitted is stamped inside the connection's send path
        // and handed back, so serialization, encryption and any queueing ahead of the probe
        // stay out of the measured round trip. A client that still stamped its own T1 could
        // not produce this: the transport's sentinel is nowhere near the current clock, so it
        // could only reach the synchronizer by having been taken from the send point.
        const long sentinelT1 = 4_242_000_000;
        var clockSync = new RecordingClockSynchronizer();
        var (client, connection, _) = TestClient.Create(configure: options => options with { ClockSynchronizer = clockSync });
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));

        connection.TimeSyncTransmitClock = () => sentinelT1;
        connection.RespondToTimeSync = true;

        await client.SendTimeSyncBurstAsync(CancellationToken.None);

        var probe = Assert.IsType<ClientTimeMessage>(connection.SnapshotSentMessages()[0]);
        Assert.Equal(sentinelT1, probe.ClientTransmitted);
        Assert.Equal(sentinelT1, Assert.Single(clockSync.Measurements).T1);
    }

    [Fact]
    public async Task TimeSyncBurst_UsesTheTransportsReceiveStamp_NotOneTakenAfterParsing()
    {
        // #227: T4 is captured in the receive loop before the frame is decrypted and parsed,
        // and plumbed through to the exchange. The fake reports a T4 that makes the round trip
        // come out at exactly TimeSyncRttMicroseconds; a T4 read after deserialization would
        // instead be "now", microseconds after T1 rather than 50 ms, so the measured round trip
        // would be nothing like this.
        const long rtt = 50_000;
        var clockSync = new RecordingClockSynchronizer();
        var (client, connection, _) = TestClient.Create(configure: options => options with { ClockSynchronizer = clockSync });
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));

        connection.TimeSyncRttMicroseconds = rtt;
        connection.RespondToTimeSync = true;

        await client.SendTimeSyncBurstAsync(CancellationToken.None);

        var m = Assert.Single(clockSync.Measurements);
        Assert.Equal(rtt, (m.T4 - m.T1) - (m.T3 - m.T2));
    }

    [Fact]
    public async Task TimeSyncBurst_AdvancesOnEachResponse_WithNoFixedInterProbeDelay()
    {
        // #225: the reference sends the next probe as soon as the previous one is answered.
        // The 50 ms spacing this replaced put a floor of 350 ms under every burst; against a
        // fixture that answers instantly a burst is now essentially free.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));
        connection.RespondToTimeSync = true;

        var stopwatch = Stopwatch.StartNew();
        await client.SendTimeSyncBurstAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(8, connection.SnapshotSentMessages().OfType<ClientTimeMessage>().Count());
        Assert.True(stopwatch.ElapsedMilliseconds < 200,
            $"A fully answered 8-probe burst took {stopwatch.ElapsedMilliseconds} ms; the old " +
            "fixed 50 ms spacing would have made it at least 350 ms.");
    }

    [Fact]
    public async Task TimeSyncBurst_NonPositiveRttSample_IsNeitherSelectedNorFedToTheFilter()
    {
        // #224: burst-best selection takes the LOWEST round trip, so a corrupt exchange whose
        // round trip comes out negative always wins — and then enters the filter with a
        // near-zero variance that drives the Kalman gain to 1. Probe 3 here is that exchange
        // (its server interval exceeds the elapsed time); the burst must fall back on the
        // seven honest samples instead.
        const long goodRtt = 4000;
        var clockSync = new RecordingClockSynchronizer();
        var (client, connection, _) = TestClient.Create(configure: options => options with { ClockSynchronizer = clockSync });
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));

        connection.RespondToTimeSync = true;
        connection.TimeSyncReplyOverride = (index, t1) => index == 3
            // (T4−T1) − (T3−T2) = 1000 − 3000 = −2000 µs.
            ? (ServerReceived: t1 + 500, ServerTransmitted: t1 + 3500, ReceivedAt: t1 + 1000)
            : (ServerReceived: t1 + (goodRtt / 2), ServerTransmitted: t1 + (goodRtt / 2) + 100,
                ReceivedAt: t1 + goodRtt + 100);

        await client.SendTimeSyncBurstAsync(CancellationToken.None);

        var m = Assert.Single(clockSync.Measurements);
        Assert.Equal(goodRtt, (m.T4 - m.T1) - (m.T3 - m.T2));
    }

    [Fact]
    public async Task TimeSyncBurst_UnansweredProbe_AdvancesToTheNextInsteadOfAbortingTheBurst()
    {
        // #225: a probe timeout advances the burst rather than ending it — one slow reply is
        // exactly when the remaining candidates are worth collecting, and the previous
        // abort-on-first-timeout turned it into a one-sample burst. Deliberately pays the real
        // per-probe timeout (10 s, the reference's DEFAULT_RESPONSE_TIMEOUT_MS): its length and
        // the advance policy are the two things this issue changed, and a shortened stand-in
        // would pin neither.
        var clockSync = new RecordingClockSynchronizer();
        var (client, connection, _) = TestClient.Create(configure: options => options with { ClockSynchronizer = clockSync });
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));

        // Probe 1 goes unanswered; every later probe is answered normally.
        connection.RespondToTimeSync = true;
        connection.TimeSyncReplyOverride = (index, t1) => index == 1
            ? null
            : (ServerReceived: t1 + 1000, ServerTransmitted: t1 + 1100, ReceivedAt: t1 + 2100);

        var burst = client.SendTimeSyncBurstAsync(CancellationToken.None);
        await WaitForAsync(
            () => connection.SnapshotSentMessages().OfType<ClientTimeMessage>().Count() == 8,
            TimeSpan.FromSeconds(20));
        await burst.WaitAsync(TimeSpan.FromSeconds(5));

        // All eight went out, and the burst still delivered a measurement from the seven that
        // were answered — where aborting would have delivered none at all.
        Assert.Equal(8, connection.SnapshotSentMessages().OfType<ClientTimeMessage>().Count());
        var m = Assert.Single(clockSync.Measurements);
        Assert.Equal(2000, (m.T4 - m.T1) - (m.T3 - m.T2));
    }

    [Fact]
    public async Task PlayerOnAGoodNetwork_ConvergesAndAnnouncesItself_WithinACoupleOfSeconds()
    {
        // #226, with the real Kalman filter rather than a scripted one: a fresh connection on
        // a well-behaved network must reach convergence — and with it the deferred initial
        // client/state carrying available: true — as promptly as the C++ and JS clients do.
        // The adaptive ladder this replaced switched to 10 s pacing at three measurements
        // while convergence needs five, so measurements four and five each arrived 10 s late
        // and a .NET player was invisible to the server for over twenty seconds after every
        // connect. Five bursts at the converging cadence is about two seconds.
        var clock = new Sendspin.SDK.Synchronization.KalmanClockSynchronizer();
        var (client, connection, _) = TestClient.Create(configure: options => options with { ClockSynchronizer = clock });
        using var _c = client;
        connection.RespondToTimeSync = true;

        var stopwatch = Stopwatch.StartNew();
        TestClient.CompleteHandshake(connection, "player@v1");

        await WaitForAsync(
            () => connection.SnapshotSentMessages().OfType<ClientStateMessage>().Any(),
            TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        var initial = Assert.Single(connection.SnapshotSentMessages().OfType<ClientStateMessage>());
        Assert.Equal(true, initial.Payload.Available);

        // The spec gate still holds: nothing was announced before the filter had converged.
        Assert.True(clock.IsConverged);
        Assert.True(clock.MeasurementCount >= 5);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4),
            $"Took {stopwatch.Elapsed.TotalSeconds:F1}s to announce availability; the reference " +
            "clients manage a couple of seconds and the pre-fix ladder took over twenty.");
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
        private readonly List<(long T1, long T2, long T3, long T4)> _measurements = new();

        public int ProcessMeasurementCallCount { get; private set; }

        /// <summary>Every exchange handed to the filter, in order.</summary>
        public IReadOnlyList<(long T1, long T2, long T3, long T4)> Measurements
        {
            get { lock (_measurements) return _measurements.ToList(); }
        }

        public bool IsConverged => false;
        public bool HasMinimalSync => false;
        public double StaticDelayMs { get; set; }

        public void ProcessMeasurement(long t1, long t2, long t3, long t4)
        {
            ProcessMeasurementCallCount++;
            lock (_measurements)
            {
                _measurements.Add((t1, t2, t3, t4));
            }
        }

        public long ClientToServerTime(long clientTime) => clientTime;
        public long ServerToClientTime(long serverTime) => serverTime;
        public long ServerToClientTimeUncompensated(long serverTime) => serverTime;
        public void Reset() { }
        public Sendspin.SDK.Synchronization.ClockSyncStatus GetStatus() => new();
    }
}
