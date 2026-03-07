using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Synchronization;

public class KalmanClockSynchronizerTests
{
    private readonly KalmanClockSynchronizer _sync = new();

    [Fact]
    public void GetStatus_Initial_ReturnsNotConverged()
    {
        var status = _sync.GetStatus();

        Assert.Equal(0, status.MeasurementCount);
        Assert.False(status.IsConverged);
        Assert.False(status.IsDriftReliable);
    }

    [Fact]
    public void GetStatus_AfterOneMeasurement_NotYetMinimalSync()
    {
        _sync.ProcessMeasurement(0, 1000, 1100, 2000);

        Assert.False(_sync.HasMinimalSync);
        var status = _sync.GetStatus();
        Assert.Equal(1, status.MeasurementCount);
        Assert.False(status.IsConverged);
    }

    [Fact]
    public void GetStatus_AfterTwoMeasurements_HasMinimalSync()
    {
        _sync.ProcessMeasurement(0, 1000, 1100, 2000);
        _sync.ProcessMeasurement(100_000, 101_000, 101_100, 102_000);

        Assert.True(_sync.HasMinimalSync);
        var status = _sync.GetStatus();
        Assert.Equal(2, status.MeasurementCount);
    }

    [Fact]
    public void GetStatus_MatchesPropertyAccessors()
    {
        // Feed enough measurements with realistic 1-second intervals
        for (int i = 0; i < 20; i++)
        {
            long t1 = i * 1_000_000L;
            long t2 = t1 + 5000;
            long t3 = t2 + 100;
            long t4 = t1 + 10_000;
            _sync.ProcessMeasurement(t1, t2, t3, t4);
        }

        var status = _sync.GetStatus();

        Assert.Equal(_sync.IsConverged, status.IsConverged);
        Assert.Equal(_sync.IsDriftReliable, status.IsDriftReliable);
        Assert.Equal(_sync.MeasurementCount, status.MeasurementCount);
        Assert.Equal(_sync.Offset, status.OffsetMicroseconds);
        Assert.Equal(_sync.Drift, status.DriftMicrosecondsPerSecond);
        Assert.Equal(_sync.OffsetUncertainty, status.OffsetUncertaintyMicroseconds);
    }

    [Fact]
    public void GetStatus_AfterManyMeasurements_Converges()
    {
        // Use realistic LAN timing: 1-second intervals, consistent 5ms offset, ~2ms RTT
        for (int i = 0; i < 50; i++)
        {
            long t1 = i * 1_000_000L;
            long t2 = t1 + 5000;  // 5ms offset
            long t3 = t2 + 100;   // 100μs server processing
            long t4 = t1 + 2000;  // ~2ms RTT
            _sync.ProcessMeasurement(t1, t2, t3, t4);
        }

        var status = _sync.GetStatus();

        Assert.True(status.IsConverged, $"Expected converged but uncertainty was {status.OffsetUncertaintyMicroseconds:F0}μs");
        Assert.True(status.MeasurementCount >= 5);
        Assert.True(status.OffsetUncertaintyMicroseconds < 1000.0);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        _sync.ProcessMeasurement(0, 5000, 5100, 10_000);
        _sync.ProcessMeasurement(100_000, 105_000, 105_100, 110_000);

        _sync.Reset();

        var status = _sync.GetStatus();
        Assert.Equal(0, status.MeasurementCount);
        Assert.False(status.IsConverged);
        Assert.Equal(0, status.OffsetMicroseconds);
    }

    [Fact]
    public void ClientToServerTime_AndBack_RoundTrips()
    {
        // Feed consistent measurements with ~5000μs offset, 1-second intervals
        for (int i = 0; i < 10; i++)
        {
            long t1 = i * 1_000_000L;
            long t2 = t1 + 5000;
            long t3 = t2 + 100;
            long t4 = t1 + 10_000;
            _sync.ProcessMeasurement(t1, t2, t3, t4);
        }

        long clientTime = 500_000L;
        long serverTime = _sync.ClientToServerTime(clientTime);
        long roundTripped = _sync.ServerToClientTime(serverTime);

        // Should round-trip within a few microseconds (rounding from double→long)
        Assert.InRange(Math.Abs(roundTripped - clientTime), 0, 5);
    }

    [Fact]
    public void StaticDelay_AffectsServerToClientTime()
    {
        _sync.ProcessMeasurement(0, 5000, 5100, 10_000);
        _sync.ProcessMeasurement(100_000, 105_000, 105_100, 110_000);

        long serverTime = 200_000L;
        long withoutDelay = _sync.ServerToClientTime(serverTime);

        _sync.StaticDelayMs = 10.0; // 10ms = 10000μs
        long withDelay = _sync.ServerToClientTime(serverTime);

        Assert.Equal(10_000, withDelay - withoutDelay);
    }
}
