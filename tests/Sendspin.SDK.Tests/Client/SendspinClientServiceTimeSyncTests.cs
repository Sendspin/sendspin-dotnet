using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

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


    [Fact]
    public async Task TimeSyncBurst_UsesTheTransportsT1_NotOneStampedBeforeTheSend()
    {
        // The probe's client_transmitted is stamped inside the connection's send path and
        // handed back, so serialization and any queueing ahead of the probe stay out of the
        // measured round trip. A client that still stamped its own T1 could not produce this:
        // the transport's sentinel is nowhere near the current clock, so it could only reach
        // the synchronizer by having been taken from the send point.
        const long sentinelT1 = 4_242_000_000;
        var (client, connection, clockSync) = CreateClient();
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
        // T4 is captured in the receive loop before the frame is parsed, and plumbed through
        // to the exchange. The fake reports a T4 that makes the round trip come out at exactly
        // TimeSyncRttMicroseconds; a T4 read after deserialization would instead be "now",
        // microseconds after T1 rather than 50 ms, so the measured round trip would be nothing
        // like this.
        const long rtt = 50_000;
        var (client, connection, clockSync) = CreateClient();
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
        // The reference sends the next probe as soon as the previous one is answered. The
        // 50 ms spacing this replaced put a floor of 350 ms under every burst; against a
        // fixture that answers instantly a burst is now essentially free.
        var (client, connection, _) = CreateClient();
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));
        connection.RespondToTimeSync = true;

        var stopwatch = Stopwatch.StartNew();
        await client.SendTimeSyncBurstAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(8, Probes(connection));
        Assert.True(stopwatch.ElapsedMilliseconds < 200,
            $"A fully answered 8-probe burst took {stopwatch.ElapsedMilliseconds} ms; the old " +
            "fixed 50 ms spacing would have made it at least 350 ms.");
    }

    [Fact]
    public async Task TimeSyncBurst_NonPositiveRttSample_IsNeitherSelectedNorFedToTheFilter()
    {
        // Burst-best selection takes the LOWEST round trip, so a corrupt exchange whose round
        // trip comes out negative always wins - and then enters the filter with a near-zero
        // variance that drives the Kalman gain to 1. Probe 3 here is that exchange (its server
        // interval exceeds the elapsed time); the burst must fall back on the seven honest
        // samples instead.
        const long goodRtt = 4000;
        var (client, connection, clockSync) = CreateClient();
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));

        connection.RespondToTimeSync = true;
        connection.TimeSyncReplyOverride = (index, t1) => index == 3
            // (T4-T1) - (T3-T2) = 1000 - 3000 = -2000 microseconds.
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
        // A probe timeout advances the burst rather than ending it - one slow reply is exactly
        // when the remaining candidates are worth collecting, and the previous
        // abort-on-first-timeout turned it into a one-sample burst. Deliberately pays the real
        // per-probe timeout (10 s, the reference's DEFAULT_RESPONSE_TIMEOUT_MS): its length and
        // the advance policy are the two things this change made, and a shortened stand-in
        // would pin neither.
        var (client, connection, clockSync) = CreateClient();
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));

        // Probe 1 goes unanswered; every later probe is answered normally.
        connection.RespondToTimeSync = true;
        connection.TimeSyncReplyOverride = (index, t1) => index == 1
            ? null
            : (ServerReceived: t1 + 1000, ServerTransmitted: t1 + 1100, ReceivedAt: t1 + 2100);

        var burst = client.SendTimeSyncBurstAsync(CancellationToken.None);
        await WaitForAsync(() => Probes(connection) == 8, TimeSpan.FromSeconds(20));
        await burst.WaitAsync(TimeSpan.FromSeconds(5));

        // All eight went out, and the burst still delivered a measurement from the seven that
        // were answered - where aborting would have delivered none at all.
        Assert.Equal(8, Probes(connection));
        var m = Assert.Single(clockSync.Measurements);
        Assert.Equal(2000, (m.T4 - m.T1) - (m.T3 - m.T2));
    }

    [Fact]
    public async Task PlayerOnAGoodNetwork_ConvergesWithinACoupleOfSeconds()
    {
        // With the real Kalman filter rather than a scripted one: a fresh connection on a
        // well-behaved network must reach convergence as promptly as the C++ and JS clients
        // do. The adaptive ladder this replaced switched to 10 s pacing at three measurements
        // while convergence needs five, so measurements four and five each arrived 10 s late
        // and a .NET player reported IsClockSynced only after twenty-odd seconds. Five bursts
        // at the converging cadence is about two seconds.
        var clock = new KalmanClockSynchronizer();
        var connection = new FakeSendspinConnection();
        await connection.ConnectAsync(new Uri("ws://test"));
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            clockSynchronizer: clock);

        connection.RespondToTimeSync = true;

        var stopwatch = Stopwatch.StartNew();
        connection.RaiseTextMessageReceived(HelloJson);

        await WaitForAsync(() => clock.IsConverged, TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        Assert.True(clock.MeasurementCount >= 5);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4),
            $"Took {stopwatch.Elapsed.TotalSeconds:F1}s to converge; the reference clients " +
            "manage a couple of seconds and the pre-fix ladder took over twenty.");
    }

    [Fact]
    public void ConvergingCadence_IsABudget_AndWidensOnALinkThatNeverConverges()
    {
        // 500 ms is the right answer for the couple of seconds a healthy link needs to converge
        // and the wrong one for a link whose noise puts the gate out of reach: uncertainty falls
        // as the square root of the sample count, so at a 100 ms round trip the gate is
        // thousands of measurements away, and an unbounded tier would sustain 5-6 probes a
        // second for the best part of an hour. The reference client does not converge there
        // either - it never leaves its fixed 10 s cadence - so the fast tier is a budget, after
        // which the interval widens whether the clock converged or not.
        var (client, _, _) = CreateClient(); // never converges, in status or in fact
        using var _c = client;

        var intervals = Enumerable.Range(0, 62).Select(_ => client.GetAdaptiveTimeSyncIntervalMs()).ToList();

        Assert.All(intervals.Take(60), interval => Assert.Equal(500, interval));
        Assert.Equal(10000, intervals[60]);
        Assert.Equal(10000, intervals[61]);
    }

    [Fact]
    public void ConvergingCadence_EndsImmediatelyOnConvergence_NotOnlyWhenTheBudgetRunsOut()
    {
        // The budget must not become the only thing that ends the fast tier - reaching the
        // convergence gate still ends it at once, which is the whole point of the tier.
        var (client, _, clockSync) = CreateClient();
        using var _c = client;

        Assert.Equal(500, client.GetAdaptiveTimeSyncIntervalMs());

        clockSync.StatusIsConverged = true;
        Assert.Equal(10000, client.GetAdaptiveTimeSyncIntervalMs());
    }

    [Fact]
    public async Task StreamStartRescueBurst_IsCancelledByDisconnect_ReleasingTheSingleBurstGuard()
    {
        // The rescue burst runs on the connection's lifetime rather than the time-sync loop's,
        // so stopping the loop cannot reach it. On CancellationToken.None it also survived the
        // disconnect: against a server that never answers client/time it kept probing for the
        // whole burst - eight probes at the 10 s per-probe timeout - holding the single-burst
        // guard, so a loop restarted by a fast reconnect had its own bursts silently skipped.
        var connection = new FakeSendspinConnection();
        var clockSync = new RecordingClockSynchronizer { StatusIsConverged = true };
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            clockSynchronizer: clockSync);

        await connection.ConnectAsync(new Uri("ws://test"));
        connection.RespondToTimeSync = true;

        // The loop's own burst completes and it settles into the steady interval, so every
        // later probe is attributable to the rescue burst alone. Measurements are recorded
        // after the guard is released, so this also proves the loop's burst is done.
        connection.RaiseTextMessageReceived(HelloJson);
        await WaitForAsync(() => clockSync.Measurements.Count > 0, TimeSpan.FromSeconds(5));
        int probesAfterLoopBurst = Probes(connection);

        // The server stops answering, then a stream starts on a clock without minimal sync.
        connection.RespondToTimeSync = false;
        connection.RaiseTextMessageReceived(StreamStartJson);
        await WaitForAsync(() => Probes(connection) == probesAfterLoopBurst + 1, TimeSpan.FromSeconds(5));

        // The connection drops with that probe still in flight.
        await connection.DisconnectAsync("network_drop");
        await Task.Delay(200);

        // The guard is free again well inside the timeout the orphan would have sat out.
        await connection.ConnectAsync(new Uri("ws://test"));
        connection.RespondToTimeSync = true;
        int probesBeforeNewBurst = Probes(connection);
        await client.SendTimeSyncBurstAsync(CancellationToken.None);

        Assert.Equal(probesBeforeNewBurst + 8, Probes(connection));
    }

    private const string HelloJson = """
        { "type": "server/hello", "payload": { "server_id": "srv-1", "version": 1, "active_roles": ["player@v1"] } }
        """;

    private const string StreamStartJson = """
        { "type": "stream/start", "payload": { "player": { "codec": "pcm", "channels": 2, "sample_rate": 48000, "bit_depth": 16 } } }
        """;

    private static int Probes(FakeSendspinConnection connection)
        => connection.SnapshotSentMessages().OfType<ClientTimeMessage>().Count();

    private static (SendspinClientService Client, FakeSendspinConnection Connection, RecordingClockSynchronizer Clock) CreateClient()
    {
        var connection = new FakeSendspinConnection();
        var clockSync = new RecordingClockSynchronizer();
        var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            clockSynchronizer: clockSync);
        return (client, connection, clockSync);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        if (!condition())
            throw new TimeoutException("Condition not met within timeout");
    }

    private sealed class RecordingClockSynchronizer : IClockSynchronizer
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

        /// <summary>
        /// Convergence as reported through <see cref="GetStatus"/>, which is what the
        /// time-sync loop paces on - scripted separately from <see cref="IsConverged"/> and
        /// <see cref="HasMinimalSync"/>, so a test can park the loop at the steady interval
        /// while the clock still looks rescue-eligible to a starting stream.
        /// </summary>
        public bool StatusIsConverged { get; set; }

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
        public void Reset() { }
        public ClockSyncStatus GetStatus() => new() { IsConverged = StatusIsConverged };
    }
}
