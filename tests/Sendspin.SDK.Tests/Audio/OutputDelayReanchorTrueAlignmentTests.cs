using Sendspin.SDK.Audio;
using Xunit.Abstractions;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Investigation repro: a push-mode device (4 800-frame buffer filled whole before Start, then
/// topped up on periodic wakes) plays a live stream through <see cref="SyncCorrectedSampleSource"/>,
/// the output delay changes mid-stream, and the host re-anchors (the buffer call AudioPipeline.ReanchorTiming makes). Alignment is measured at the device's exit, from the frame
/// identities that actually leave it — not from anything the buffer reports.
/// </summary>
public class OutputDelayReanchorTrueAlignmentTests
{
    private readonly ITestOutputHelper _output;

    public OutputDelayReanchorTrueAlignmentTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(0, 40, 10, 100, false)]
    [InlineData(0, 40, 50, 100, false)]
    [InlineData(0, 500, 10, 100, false)]
    [InlineData(0, 500, 50, 100, false)]
    [InlineData(0, 40, 10, 100, true)]
    [InlineData(0, 40, 50, 100, true)]
    [InlineData(0, 40, 10, 0, false)]
    [InlineData(0, 40, 50, 0, false)]
    [InlineData(0, 500, 10, 0, false)]
    [InlineData(0, 500, 50, 0, false)]
    [InlineData(100, 60, 10, 0, false)]
    [InlineData(700, 0, 50, 0, false)]
    public void OutputDelayChange_ThenReanchor_MovesPlaybackByExactlyTheDelay(
        int initialDelayMs, int delayMs, int wakeMs, int reportedLatencyMs, bool jitter)
    {
        using var player = new PushModeDevicePlayer(wakeMs, reportedLatencyMs, jitter, initialDelayMs);

        player.Run(5_000);
        var before = player.Measure(500);
        var reportedBefore = player.Buffer.SyncErrorMicroseconds;
        var bufferedBefore = player.Buffer.BufferedMilliseconds;

        player.ClockSync.OutputDelayMs = delayMs;
        player.Run(300);
        player.Reanchor();
        player.Run(6_000);
        var after = player.Measure(500);

        _output.WriteLine(
            $"delay {initialDelayMs}->{delayMs}ms wake {wakeMs}ms reportedLatency {reportedLatencyMs}ms jitter {jitter}");
        foreach (var entry in player.Log.Entries.Where(e => e.Message.Contains("[Correction]") || e.Message.Contains("Skipped")))
        {
            _output.WriteLine("  log: " + entry.Message);
        }

        _output.WriteLine(
            $"  BEFORE: true lateness median {before.Median / 1000.0:+0.00;-0.00}ms (p1 {before.P1 / 1000.0:+0.00;-0.00}, p99 {before.P99 / 1000.0:+0.00;-0.00}), " +
            $"reported {reportedBefore / 1000.0:+0.00;-0.00}ms, buffered {bufferedBefore:F0}ms");
        _output.WriteLine(
            $"  AFTER : true lateness median {after.Median / 1000.0:+0.00;-0.00}ms (p1 {after.P1 / 1000.0:+0.00;-0.00}, p99 {after.P99 / 1000.0:+0.00;-0.00}), " +
            $"reported {player.Buffer.SyncErrorMicroseconds / 1000.0:+0.00;-0.00}ms, " +
            $"buffered {player.Buffer.BufferedMilliseconds:F0}ms, device underruns {player.DeviceUnderruns}, re-anchors {player.Buffer.GetStats().ReanchorCount}");

        // Lateness is measured against ServerToClientTime, which already carries the delay, so
        // a re-anchor that moved playback by exactly the delay leaves it where it was.
        var moved = after.Median - before.Median;
        Assert.True(
            Math.Abs(moved) < 1_000,
            $"The re-anchor moved playback {moved / 1000.0:+0.00;-0.00}ms beyond the requested delay change: " +
            $"{before.Median / 1000.0:+0.00;-0.00}ms from schedule before, {after.Median / 1000.0:+0.00;-0.00}ms after, " +
            $"while the buffer reports {player.Buffer.SyncErrorMicroseconds / 1000.0:+0.00;-0.00}ms.");

        // With no latency reported, this device's cold start is on schedule, so the absolute
        // figure can be held to the spec's 1 ms as well.
        if (reportedLatencyMs == 0)
        {
            Assert.True(
                Math.Abs(after.Median) < 1_000,
                $"Audio leaves the device {after.Median / 1000.0:+0.00;-0.00}ms from its schedule after the re-anchor.");
        }
    }

    /// <summary>
    /// A live stream holds almost nothing: here about 80 ms. A +500 ms delay asks for audio the
    /// server has not sent yet, so the only honest outcome is silence until it has, and then
    /// playback exactly where it was against the schedule — no re-anchor, no residue.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(0)]
    public void DelayChangeLargerThanTheBufferedAudio_GoesSilentThenResumesAligned(int reportedLatencyMs)
    {
        // Cold start reads one device buffer ahead, plus the pre-roll: lead the server by that
        // much and 80 ms more.
        var lead = (180 + reportedLatencyMs) * 1000L;
        using var player = new PushModeDevicePlayer(wakeMs: 10, reportedLatencyMs, jitter: false, initialDelayMs: 0, lead);

        player.Run(5_000);
        var before = player.Measure(500);
        var bufferedBefore = player.Buffer.BufferedMilliseconds;
        Assert.InRange(bufferedBefore, 60, 100);

        player.ClockSync.OutputDelayMs = 500;
        player.Run(300);
        player.Reanchor();

        // The server takes 200 ms to start sending 500 ms further ahead.
        player.Run(200);
        var underrunsWhileDry = player.Buffer.GetStats().UnderrunCount;
        player.LeadMicroseconds = lead + 500_000;
        player.Run(6_000);
        var after = player.Measure(500);
        var stats = player.Buffer.GetStats();

        _output.WriteLine(
            $"reportedLatency {reportedLatencyMs}ms: buffered {bufferedBefore:F0}ms -> {stats.BufferedMs:F0}ms, " +
            $"true lateness {before.Median / 1000.0:+0.00;-0.00}ms -> {after.Median / 1000.0:+0.00;-0.00}ms, " +
            $"reported {stats.SyncErrorMicroseconds / 1000.0:+0.00;-0.00}ms, underruns while dry {underrunsWhileDry}, " +
            $"re-anchors {stats.ReanchorCount}, hard syncs {stats.HardSyncCount}");
        foreach (var entry in player.Log.Entries.Where(e => e.Message.Contains("[Correction]") || e.Message.Contains("Skipped")))
        {
            _output.WriteLine("  log: " + entry.Message);
        }

        Assert.True(underrunsWhileDry > 0, "the buffer should have run dry while the server caught up");
        Assert.Equal(0, stats.ReanchorCount);
        Assert.InRange(after.Median - before.Median, -1_000, 1_000);
        Assert.InRange(stats.BufferedMs, bufferedBefore - 25, bufferedBefore + 25);
    }

    /// <summary>
    /// A live server answers a larger delay by moving its timeline later by the same amount, so
    /// the audio that follows is due exactly when it would have been. What is already buffered
    /// is stale and goes; nothing else should. A skip sized for a continuous timeline must not
    /// carry on into audio the timestamp step has already put on schedule.
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(500)]
    public void DelayIncrease_WhenTheServerShiftsItsTimeline_LosesOnlyTheStaleAudio(int increaseMs)
    {
        using var player = new PushModeDevicePlayer(wakeMs: 10, reportedLatencyMs: 0, jitter: false, initialDelayMs: 0, 180_000);

        player.Run(3_000);
        var before = player.Measure(500);
        var buffered = player.Buffer.BufferedMilliseconds;
        var audioBefore = player.AudioExitedMs;

        player.ClockSync.OutputDelayMs = increaseMs;
        player.ServerShiftsTimelineLater(increaseMs * 1000L);
        player.Reanchor();
        player.Run(4_000);
        var after = player.Measure(500);
        var lost = 4_000 - (player.AudioExitedMs - audioBefore);
        var stats = player.Buffer.GetStats();

        _output.WriteLine(
            $"+{increaseMs}ms with {buffered:F0}ms buffered: true lateness {before.Median / 1000.0:+0.00;-0.00}ms -> " +
            $"{after.Median / 1000.0:+0.00;-0.00}ms, audio lost {lost:F0}ms, re-anchors {stats.ReanchorCount}");
        foreach (var entry in player.Log.Entries.Where(e => e.Message.Contains("[Correction]") || e.Message.Contains("timeline")))
        {
            _output.WriteLine("  log: " + entry.Message);
        }

        Assert.Equal(0, stats.ReanchorCount);
        Assert.InRange(after.Median - before.Median, -1_000, 1_000);

        // The stale audio goes, and the same stretch of wall clock is silent before the shifted
        // audio falls due: twice the smaller of the change and what was buffered, plus margin.
        var unavoidable = 2 * Math.Min(increaseMs, buffered);
        Assert.InRange(lost, 0, unavoidable + 40);
    }

    /// <summary>
    /// A delay decrease early in a stream plays silence for longer than the startup grace
    /// lasts. The baseline is taken at the end of that grace and absorbs whatever error it
    /// finds, so taking it part way through the silence would keep the unplayed remainder as
    /// a constant.
    /// </summary>
    [Theory]
    [InlineData(400, 200, 10)]
    [InlineData(280, 250, 10)]
    [InlineData(280, 250, 2)]
    [InlineData(400, 100, 2)]
    [InlineData(200, 300, 1)]
    public void DelayDecreaseDuringTheStartupGrace_IsNotAbsorbedIntoTheBaseline(int initialDelayMs, int changeAtMs, int wakeMs)
    {
        using var player = new PushModeDevicePlayer(wakeMs, reportedLatencyMs: 0, jitter: false, initialDelayMs);

        player.Run(changeAtMs);
        player.ClockSync.OutputDelayMs = 0;
        player.Reanchor();
        player.Run(5_000);
        var after = player.Measure(500);
        var stats = player.Buffer.GetStats();

        _output.WriteLine(
            $"true lateness {after.Median / 1000.0:+0.00;-0.00}ms, reported {stats.SyncErrorMicroseconds / 1000.0:+0.00;-0.00}ms, " +
            $"hard syncs {stats.HardSyncCount}, re-anchors {stats.ReanchorCount}");
        foreach (var entry in player.Log.Entries.Where(e => e.Message.Contains("[Correction]")))
        {
            _output.WriteLine("  log: " + entry.Message);
        }

        Assert.Equal(0, stats.ReanchorCount);
        Assert.InRange(after.Median, -1_000, 1_000);
    }
}
