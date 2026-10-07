using Xunit.Abstractions;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Where playback lands on a push-mode device that is topped up to the brim on every wake, when
/// the player reports what is queued in the device (<c>IAudioPlayer.GetCurrentOutputLatencyMicroseconds</c>).
/// </summary>
/// <remarks>
/// A frame handed to such a device waits behind whatever is queued ahead of it: nothing on the
/// first fill of an empty device, a buffer less one wake from then on. The reported latency (the
/// buffer's depth, 100 ms here) is right for neither, so a start that was already late played
/// early by the whole buffer and a start scheduled ahead played early by one wake. Alignment is
/// measured from the frames that actually leave the modelled queue, never from the buffer's own
/// error.
/// </remarks>
public class LateStartDeviceQueueAlignmentTests
{
    private const int ReportedLatencyMs = 100;

    private readonly ITestOutputHelper _output;

    public LateStartDeviceQueueAlignmentTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <param name="scenario">Label for the output.</param>
    /// <param name="startDueInMs">
    /// When the first frame's schedule as pre-rolled by the reported latency falls, relative to the
    /// device's first read: positive is still ahead, negative already past.
    /// </param>
    /// <param name="wakeMs">Device wake period.</param>
    /// <param name="splitReads">Whether each device request arrives as several unequal reads.</param>
    [Theory]
    [InlineData("a: scheduled 1 s ahead", 1_000, 10, false)]
    [InlineData("b: 10 ms past due", -10, 10, false)]
    [InlineData("c: 150 ms past due", -150, 10, false)]
    [InlineData("a: scheduled 1 s ahead", 1_000, 50, false)]
    [InlineData("b: 10 ms past due", -10, 50, false)]
    [InlineData("c: 150 ms past due", -150, 50, false)]
    [InlineData("a: scheduled 1 s ahead", 1_000, 10, true)]
    [InlineData("b: 10 ms past due", -10, 10, true)]
    [InlineData("c: 150 ms past due", -150, 10, true)]
    [InlineData("a: scheduled 1 s ahead", 1_000, 50, true)]
    [InlineData("b: 10 ms past due", -10, 50, true)]
    [InlineData("c: 150 ms past due", -150, 50, true)]
    public void Start_WithTheDeviceQueueMeasured_LandsOnSchedule(
        string scenario, int startDueInMs, int wakeMs, bool splitReads)
    {
        var (first, settled) = RunStart(scenario, startDueInMs, wakeMs, splitReads, measuresQueue: true);

        // Where the start lands is what the measurement decides, and it is held to the spec's 1 ms.
        Assert.InRange(first, -1_000, 1_000);

        // Split reads have a cost of their own afterwards, with or without the measurement: the
        // error is taken after every read, so part way through a request it reads as late by the
        // part not yet read, and the correction tiers settle up to about 2.5 ms off chasing it
        // when the request is 50 ms long. That is the same size with no measurement at all.
        var settledTolerance = splitReads ? 3_000 : 1_000;
        Assert.InRange(settled, -settledTolerance, settledTolerance);
    }

    /// <summary>
    /// The delay shift is independent of how the anchor was pre-rolled: with the measurement on,
    /// a delay change still moves playback by exactly the delay, and playback is on schedule on
    /// both sides of it.
    /// </summary>
    [Theory]
    [InlineData(0, 40, 10)]
    [InlineData(0, 500, 10)]
    [InlineData(700, 0, 10)]
    [InlineData(0, 40, 50)]
    [InlineData(0, 500, 50)]
    [InlineData(700, 0, 50)]
    public void OutputDelayChange_WithTheDeviceQueueMeasured_MovesPlaybackByExactlyTheDelay(
        int initialDelayMs, int delayMs, int wakeMs)
    {
        using var player = new PushModeDevicePlayer(
            wakeMs, ReportedLatencyMs, jitter: false, initialDelayMs, measuresQueue: true);

        player.Run(5_000);
        var before = player.Measure(500).Median;

        player.ClockSync.OutputDelayMs = delayMs;
        player.Run(300);
        player.Reanchor();
        player.Run(6_000);
        var after = player.Measure(500).Median;

        _output.WriteLine(
            $"delay {initialDelayMs}->{delayMs}ms wake {wakeMs}ms: true lateness {before / 1000.0:+0.00;-0.00}ms -> " +
            $"{after / 1000.0:+0.00;-0.00}ms, re-anchors {player.Buffer.GetStats().ReanchorCount}");

        Assert.Equal(0, player.Buffer.GetStats().ReanchorCount);
        Assert.InRange(after - before, -1_000, 1_000);
        Assert.InRange(before, -1_000, 1_000);
        Assert.InRange(after, -1_000, 1_000);
    }

    /// <summary>
    /// A live server that answers a larger delay by stepping its timeline later, with the
    /// measurement on.
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(500)]
    public void DelayIncrease_WhenTheServerShiftsItsTimeline_WithTheDeviceQueueMeasured_StaysOnSchedule(int increaseMs)
    {
        using var player = new PushModeDevicePlayer(
            wakeMs: 10, ReportedLatencyMs, jitter: false, initialDelayMs: 0, leadMicroseconds: 180_000, measuresQueue: true);

        player.Run(3_000);
        var before = player.Measure(500).Median;

        player.ClockSync.OutputDelayMs = increaseMs;
        player.ServerShiftsTimelineLater(increaseMs * 1000L);
        player.Reanchor();
        player.Run(4_000);
        var after = player.Measure(500).Median;

        _output.WriteLine(
            $"rebasing server +{increaseMs}ms: true lateness {before / 1000.0:+0.00;-0.00}ms -> {after / 1000.0:+0.00;-0.00}ms, " +
            $"re-anchors {player.Buffer.GetStats().ReanchorCount}");

        Assert.Equal(0, player.Buffer.GetStats().ReanchorCount);
        Assert.InRange(after - before, -1_000, 1_000);
        Assert.InRange(after, -1_000, 1_000);
    }

    private (double First, double Settled) RunStart(
        string scenario, int startDueInMs, int wakeMs, bool splitReads, bool measuresQueue)
    {
        var firstTimestampOffset = (ReportedLatencyMs + startDueInMs) * 1000L;
        using var player = new PushModeDevicePlayer(
            wakeMs,
            ReportedLatencyMs,
            jitter: false,
            initialDelayMs: 0,
            leadMicroseconds: firstTimestampOffset + 1_500_000,
            firstTimestampOffsetMicroseconds: firstTimestampOffset,
            measuresQueue: measuresQueue,
            splitReads: splitReads);

        player.Run(6_000);
        var first = player.MeasureFirst(100);
        var settled = player.Measure(500);
        var stats = player.Buffer.GetStats();

        _output.WriteLine(
            $"{scenario}, wake {wakeMs}ms, split {splitReads}, measured {measuresQueue}: " +
            $"first audio out at +{player.FirstAudioExitMicroseconds / 1000.0:F1}ms, " +
            $"true lateness first 100ms {first.Median / 1000.0:+0.00;-0.00}ms, settled {settled.Median / 1000.0:+0.00;-0.00}ms " +
            $"(p1 {settled.P1 / 1000.0:+0.00;-0.00}, p99 {settled.P99 / 1000.0:+0.00;-0.00}), " +
            $"reported {stats.SyncErrorMicroseconds / 1000.0:+0.00;-0.00}ms, hard syncs {stats.HardSyncCount}, re-anchors {stats.ReanchorCount}");
        foreach (var entry in player.Log.Entries.Where(e => e.Message.Contains("[Correction]") || e.Message.Contains("Skipped")))
        {
            _output.WriteLine("  log: " + entry.Message);
        }

        return (first.Median, settled.Median);
    }
}
