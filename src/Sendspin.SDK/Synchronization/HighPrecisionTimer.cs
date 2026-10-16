// <copyright file="HighPrecisionTimer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sendspin.SDK.Synchronization;

/// <summary>
/// Provides high-precision time measurement using hardware performance counters.
/// </summary>
/// <remarks>
/// <para>
/// This class provides microsecond-precision timing by combining:
/// - System UTC time for absolute reference (synchronized once at startup)
/// - A monotonic counter for precise incremental timing between measurements
/// </para>
/// <para>
/// Why not use DateTimeOffset.UtcNow directly?
/// - DateTime resolution on Windows is typically ~15ms (system timer interrupt)
/// - Stopwatch uses QueryPerformanceCounter with ~100ns precision
/// - For audio synchronization, we need microsecond-level accuracy
/// </para>
/// <para>
/// On Linux the counter is <c>CLOCK_MONOTONIC_RAW</c> rather than Stopwatch, which reads
/// <c>CLOCK_MONOTONIC</c> there. NTP slews <c>CLOCK_MONOTONIC</c>: while the time daemon corrects
/// the system clock it runs tens of ppm fast or slow, and an initial synchronisation can move it
/// by milliseconds within seconds. aiosendspin timestamps on <c>CLOCK_MONOTONIC_RAW</c>, so a
/// slewed client clock shows up as clock drift that changes faster than the clock synchroniser
/// models, and audio falls out of sync whenever the host's time daemon is correcting. On Windows
/// (QueryPerformanceCounter) and macOS (<c>CLOCK_UPTIME_RAW</c>) Stopwatch is already unslewed.
/// </para>
/// </remarks>
public sealed class HighPrecisionTimer : IHighPrecisionTimer
{
    /// <summary>
    /// Whether the timer counts on <c>CLOCK_MONOTONIC_RAW</c> rather than Stopwatch.
    /// </summary>
    /// <remarks>
    /// Chosen once, so every timer in the process counts on the same clock. Declared above
    /// <see cref="Shared"/>: static initializers run in order and its constructor reads this.
    /// </remarks>
    internal static readonly bool UsesRawMonotonicClock = OperatingSystem.IsLinux() && RawMonotonicClock.IsAvailable();

    private const long NanosecondsPerSecond = 1_000_000_000;

    private readonly long _startTimestampTicks;
    private readonly long _startTimeUnixMicroseconds;
    private readonly double _ticksToMicroseconds;

    /// <summary>
    /// Gets the shared instance of the high-precision timer.
    /// </summary>
    /// <remarks>
    /// Using a singleton ensures all components share the same time base,
    /// avoiding drift between different timer instances.
    /// </remarks>
    public static IHighPrecisionTimer Shared { get; } = new HighPrecisionTimer();

    /// <summary>
    /// Initializes a new instance of the <see cref="HighPrecisionTimer"/> class.
    /// </summary>
    public HighPrecisionTimer()
    {
        // Capture both the counter and system time as close together as possible
        // to minimize the offset between them
        _startTimestampTicks = GetTimestamp();
        _startTimeUnixMicroseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

        // Pre-calculate conversion factor for ticks to microseconds
        _ticksToMicroseconds = 1_000_000.0 / TicksPerSecond;
    }

    private static long TicksPerSecond => UsesRawMonotonicClock ? NanosecondsPerSecond : Stopwatch.Frequency;

    /// <inheritdoc/>
    public long GetCurrentTimeMicroseconds()
    {
        var currentTicks = GetTimestamp();
        var elapsedTicks = currentTicks - _startTimestampTicks;
        var elapsedMicroseconds = (long)(elapsedTicks * _ticksToMicroseconds);

        return _startTimeUnixMicroseconds + elapsedMicroseconds;
    }

    /// <inheritdoc/>
    public long GetElapsedMicroseconds(long fromTimeMicroseconds)
    {
        return GetCurrentTimeMicroseconds() - fromTimeMicroseconds;
    }

    /// <summary>
    /// Gets the resolution of the timer in nanoseconds.
    /// </summary>
    /// <returns>Timer resolution in nanoseconds.</returns>
    public static double GetResolutionNanoseconds()
    {
        return 1_000_000_000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// Gets whether the underlying hardware supports high-resolution timing.
    /// </summary>
    public static bool IsHighResolution => Stopwatch.IsHighResolution;

    // Stopwatch on Linux also counts nanoseconds since boot, so falling back on a failed read
    // (which the probe in UsesRawMonotonicClock rules out) costs only the slew accumulated so far.
    private static long GetTimestamp() =>
        UsesRawMonotonicClock && RawMonotonicClock.TryGetNanoseconds(out var nanoseconds)
            ? nanoseconds
            : Stopwatch.GetTimestamp();

    /// <summary>
    /// <c>clock_gettime(CLOCK_MONOTONIC_RAW)</c>. The runtime maps "libc" to the platform's C
    /// library for both JIT and NativeAOT, so this resolves on glibc and musl alike.
    /// </summary>
    private static class RawMonotonicClock
    {
        private const int ClockMonotonicRaw = 4;

        public static bool IsAvailable()
        {
            try
            {
                return TryGetNanoseconds(out _);
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        public static bool TryGetNanoseconds(out long nanoseconds)
        {
            if (ClockGetTime(ClockMonotonicRaw, out var ts) != 0)
            {
                nanoseconds = 0;
                return false;
            }

            nanoseconds = ((long)ts.Seconds * NanosecondsPerSecond) + ts.Nanoseconds;
            return true;
        }

        [DllImport("libc", EntryPoint = "clock_gettime")]
        private static extern int ClockGetTime(int clockId, out Timespec timespec);

        // time_t and long are both pointer-sized on the ABIs whose clock_gettime this binds.
        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec
        {
            public nint Seconds;
            public nint Nanoseconds;
        }
    }
}

/// <summary>
/// Interface for high-precision time measurement.
/// </summary>
public interface IHighPrecisionTimer
{
    /// <summary>
    /// Gets the current time in microseconds since Unix epoch.
    /// </summary>
    /// <returns>Current time in microseconds.</returns>
    long GetCurrentTimeMicroseconds();

    /// <summary>
    /// Gets the elapsed time since a given timestamp.
    /// </summary>
    /// <param name="fromTimeMicroseconds">Starting time in microseconds.</param>
    /// <returns>Elapsed time in microseconds.</returns>
    long GetElapsedMicroseconds(long fromTimeMicroseconds);
}
