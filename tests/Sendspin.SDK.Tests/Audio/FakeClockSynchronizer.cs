using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Deterministic clock synchronizer for buffer timing tests.
/// Before "sync" (OffsetMicroseconds unset), behaves like the real Kalman
/// synchronizer with zero measurements: ServerToClientTime returns the raw
/// server timestamp. Setting OffsetMicroseconds simulates sync convergence.
/// </summary>
internal sealed class FakeClockSynchronizer : IClockSynchronizer
{
    /// <summary>
    /// Offset applied in conversions: client_time = server_time + offset.
    /// Zero mimics the pre-sync state (raw server timestamps pass through).
    /// </summary>
    public long OffsetMicroseconds { get; set; }

    public bool IsConverged { get; set; }

    public bool HasMinimalSync { get; set; }

    public double StaticDelayMs { get; set; }

    public long ServerToClientTime(long serverTime) =>
        serverTime + OffsetMicroseconds - (long)(StaticDelayMs * 1000);

    public long ClientToServerTime(long clientTime) =>
        clientTime - OffsetMicroseconds + (long)(StaticDelayMs * 1000);

    public void ProcessMeasurement(long t1, long t2, long t3, long t4)
    {
    }

    public void Reset()
    {
        OffsetMicroseconds = 0;
        IsConverged = false;
        HasMinimalSync = false;
    }

    public ClockSyncStatus GetStatus() => new()
    {
        OffsetMicroseconds = OffsetMicroseconds,
        IsConverged = IsConverged,
    };
}
