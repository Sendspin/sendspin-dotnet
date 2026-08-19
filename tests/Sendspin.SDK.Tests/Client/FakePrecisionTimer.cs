using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Local clock a test holds still (or moves by hand), in the same microsecond base
/// <see cref="IClockSynchronizer.ServerToClientTime"/> converts into.
/// </summary>
/// <remarks>
/// Display scheduling compares a translated timestamp against this clock, so freezing it is what
/// makes "already due" and "still in the future" decidable without waiting: with
/// <see cref="ConvergedClockSynchronizer"/>'s zero offset, a frame stamped
/// <see cref="CurrentTime"/> is due exactly now, and one stamped later is held. A test that needs
/// a scheduled item to actually fire must use the real clock instead — a frozen clock never
/// reaches a future deadline, which is precisely what the "held" assertions rely on.
/// </remarks>
internal sealed class FakePrecisionTimer : IHighPrecisionTimer
{
    /// <summary>Gets or sets the value the clock reports, in microseconds since the Unix epoch.</summary>
    public long CurrentTime { get; set; }

    public long GetCurrentTimeMicroseconds() => CurrentTime;

    public long GetElapsedMicroseconds(long fromTimeMicroseconds) => CurrentTime - fromTimeMicroseconds;
}
