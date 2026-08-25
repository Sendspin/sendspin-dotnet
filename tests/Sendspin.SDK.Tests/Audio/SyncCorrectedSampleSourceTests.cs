using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// The smooth correction chain: a resampler driven from
/// <see cref="ITimedAudioBuffer.ReadRaw"/> and <see cref="ISyncCorrectionProvider"/>. These cover
/// the two artefacts the chain shipped with — the click at the rate-1.0 boundary and the silence
/// gap on a mid-callback shortfall, both from windowsSpin issue #63 — plus the invariants that make
/// the correction conformant: the ±0.5% cap, an exact passthrough inside the dead band, and
/// neutrality while the buffer's one-shot snap is in flight.
/// </summary>
public class SyncCorrectedSampleSourceTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    /// <summary>One 10 ms callback at 48 kHz stereo.</summary>
    private const int CallbackSamples = 960;

    private static readonly AudioFormat Format = new()
    {
        Codec = "pcm",
        SampleRate = SampleRate,
        Channels = Channels,
    };

    // ── Defect 1: the click at the rate-1.0 boundary ────────────────────────

    /// <summary>
    /// Regression test for windowsSpin issue #63's audible click.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordinary clock drift makes the corrector cycle between no correction and a small rate
    /// trim — the error crosses the dead band, the rate leaves 1.0, the error closes, the rate
    /// returns to exactly 1.0 — many times a minute. Anything that treats rate 1.0 as "resampler
    /// not needed" pays for that cycle in discontinuities: a bypass strands the input and the
    /// fractional read position the resampler is holding, and WDL's IIR low-pass chain, which runs
    /// only while the ratio is off unity and never clears its history, re-engages against
    /// seconds-stale state. Either one manufactures a step in the waveform, which is a click.
    /// </para>
    /// <para>
    /// A 101 Hz sine at amplitude 0.5 moves at most ~0.0066 per sample, so any output step well
    /// above that bound was manufactured rather than played. 101 Hz and the 47-callback toggle
    /// period are chosen incommensurate, so stale state is never accidentally phase-aligned with
    /// the live signal.
    /// </para>
    /// </remarks>
    [Fact]
    public void RateCorrection_TogglingAcrossUnity_DoesNotProduceClicks()
    {
        const double frequency = 101.0;
        const double amplitude = 0.5;

        var buffer = SignalBuffer.Sine(frequency, amplitude);
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        var maxDelta = MeasureMaxSampleDelta(
            source,
            callbacks: 1000,
            rateForCallback: cb => (cb / 47) % 2 == 0 ? 1.0 : 1.005,
            provider);

        var slopeBound = amplitude * 2 * Math.PI * frequency / SampleRate;
        Assert.True(
            maxDelta < 3 * slopeBound,
            $"output stepped by {maxDelta:F5} between adjacent samples (sine slope bound " +
            $"{slopeBound:F5}) — the issue #63 click is back");
    }

    /// <summary>
    /// The same guard for the slow-down direction, and across the dead band in both directions
    /// within one run: 1.0 → 1.005 → 1.0 → 0.995 → 1.0. A fix that only kept state warm while
    /// speeding up would pass the test above and fail here.
    /// </summary>
    [Fact]
    public void RateCorrection_TogglingBothDirections_DoesNotProduceClicks()
    {
        const double frequency = 101.0;
        const double amplitude = 0.5;

        var buffer = SignalBuffer.Sine(frequency, amplitude);
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        var cycle = new[] { 1.0, 1.005, 1.0, 0.995 };
        var maxDelta = MeasureMaxSampleDelta(
            source,
            callbacks: 1000,
            rateForCallback: cb => cycle[(cb / 31) % cycle.Length],
            provider);

        var slopeBound = amplitude * 2 * Math.PI * frequency / SampleRate;
        Assert.True(
            maxDelta < 3 * slopeBound,
            $"output stepped by {maxDelta:F5} between adjacent samples (sine slope bound {slopeBound:F5})");
    }

    // ── Defect 2: the silence gap on a mid-callback shortfall ───────────────

    /// <summary>
    /// Ported from windowsSpin's <c>RateCorrection_ConcealsShortfalls_WithoutSilenceGaps</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under continuous drift correction the buffer regularly hands back a little less than a
    /// callback's worth — the resampler wants a fraction of a frame more input than a whole-frame
    /// read can give it, and the buffer itself runs briefly dry between chunk arrivals. Filling
    /// that residual with digital silence puts a bit-exact zero into otherwise-continuous audio,
    /// which is a step to zero and back: a broadband click, and one that fires tens of times a
    /// second (861 events in 21 s were observed on the reporter's USB DAC).
    /// </para>
    /// <para>
    /// The signal is DC, which a correct resampler passes through unchanged, so an exact-zero
    /// output sample is the unambiguous signature of a leaked silence pad — nothing else in the
    /// chain can produce one.
    /// </para>
    /// </remarks>
    [Fact]
    public void Shortfall_MidCallback_IsConcealed_WithoutSilenceGaps()
    {
        const float dc = 0.5f;

        var buffer = SignalBuffer.Constant(dc);
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        var output = new float[CallbackSamples];
        var silenceSamples = 0;

        // Warm-up: the very first callbacks legitimately produce nothing until the resampler is
        // primed, and silence is the right answer for a callback with no content at all.
        for (var i = 0; i < 5; i++)
        {
            source.Read(output, 0, CallbackSamples);
        }

        var rate = 1.0;
        var step = 0.00005;
        for (var cb = 0; cb < 300; cb++)
        {
            rate += step;
            if (rate is > 1.002 or < 0.998)
            {
                step = -step;
            }

            provider.SetResampling(rate);

            // The buffer is four frames short of what this callback needs — content that has not
            // arrived yet, not a stall. It is made good on the next callback.
            buffer.AvailableSamples = CallbackSamples - (4 * Channels);
            source.Read(output, 0, CallbackSamples);

            foreach (var sample in output)
            {
                if (sample == 0f)
                {
                    silenceSamples++;
                }
            }
        }

        Assert.True(source.ConcealedFrameCount > 0, "the run never hit the shortfall path");
        Assert.Equal(0, silenceSamples);
    }

    /// <summary>
    /// The opposite case, and the reason concealment is not unconditional: a callback that
    /// produced nothing at all is a sustained stall, and silence is the right answer. Holding the
    /// last sample across it would park a DC offset on the speaker and thump when it released.
    /// </summary>
    [Fact]
    public void SustainedStall_FillsSilence_AndCountsAnUnderrun()
    {
        var buffer = SignalBuffer.Constant(0.5f);
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        var output = new float[CallbackSamples];
        for (var i = 0; i < 5; i++)
        {
            source.Read(output, 0, CallbackSamples);
        }

        buffer.AvailableSamples = 0;

        var realSamples = source.Read(output, 0, CallbackSamples);

        Assert.Equal(0, realSamples);
        Assert.All(output, sample => Assert.Equal(0f, sample));
        Assert.Equal(1, source.UnderrunCount);
    }

    // ── The ±0.5% cap ───────────────────────────────────────────────────────

    /// <summary>
    /// The spec's cap is a fleet-homogeneity contract (roles/player/v1.md:134), so it is enforced
    /// here as well as in the provider: a custom <see cref="ISyncCorrectionProvider"/> asking for
    /// 1.5x cannot take this player out of spec.
    /// </summary>
    [Theory]
    [InlineData(1.5)]
    [InlineData(0.5)]
    [InlineData(1.02)]
    [InlineData(0.98)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RogueProviderRate_IsClampedToTheSpecCap(double reportedRate)
    {
        var options = SyncCorrectionOptions.Default;
        var buffer = SignalBuffer.Constant(0.25f);
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        provider.SetResampling(reportedRate);
        source.Read(new float[CallbackSamples], 0, CallbackSamples);

        Assert.InRange(source.PlaybackRate, options.MinRate, options.MaxRate);
        Assert.InRange(buffer.LastReportedRate, options.MinRate, options.MaxRate);
    }

    /// <summary>
    /// And the same through the shipped provider: an error far larger than any correction band
    /// still resolves to a rate inside the cap.
    /// </summary>
    [Theory]
    [InlineData(4_000)]
    [InlineData(-4_000)]
    public void ExtremeError_ThroughTheCalculator_StaysWithinTheSpecCap(long errorMicroseconds)
    {
        var options = SyncCorrectionOptions.Default;
        var calculator = new SyncCorrectionCalculator(options, SampleRate, Channels);

        // Past the startup grace period, so corrections are live.
        calculator.NotifySamplesProcessed(SampleRate * Channels);
        calculator.UpdateFromSyncError(errorMicroseconds, errorMicroseconds);

        Assert.Equal(SyncCorrectionMode.Resampling, calculator.CurrentMode);
        Assert.InRange(calculator.TargetPlaybackRate, options.MinRate, options.MaxRate);

        var buffer = SignalBuffer.Constant(0.25f);
        buffer.SyncError = errorMicroseconds;
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, calculator);

        for (var i = 0; i < 10; i++)
        {
            source.Read(new float[CallbackSamples], 0, CallbackSamples);
        }

        Assert.InRange(source.PlaybackRate, options.MinRate, options.MaxRate);
    }

    // ── Dead band and hard sync ─────────────────────────────────────────────

    /// <summary>
    /// Inside the dead band the rate is exactly 1.0 and the chain must be transparent — not
    /// "close enough", bit for bit. At an identity ratio WDL's linear interpolation reads every
    /// output frame at fraction 0.0, so the samples come through untouched; anything else would
    /// mean the chain is colouring audio that needs no correction at all.
    /// </summary>
    [Fact]
    public void DeadbandSteadyState_IsBitIdenticalPassthrough()
    {
        var buffer = SignalBuffer.Sine(997.0, 0.8);
        var expected = SignalBuffer.Sine(997.0, 0.8);
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        var actualOutput = new float[CallbackSamples];
        var expectedOutput = new float[CallbackSamples];

        for (var cb = 0; cb < 50; cb++)
        {
            source.Read(actualOutput, 0, CallbackSamples);
            expected.ReadRaw(expectedOutput, 0);

            Assert.Equal(expectedOutput, actualOutput);
        }
    }

    /// <summary>
    /// While the buffer's one-shot snap is actually in flight an external corrector must stand
    /// down: the snap is a buffer-timeline operation, and correcting on top of it corrects the
    /// same error twice. Gated on the buffer, not on the provider's prediction, and enforced
    /// rather than trusted — a custom provider can report any rate it likes.
    /// </summary>
    [Fact]
    public void SnapInFlight_HoldsTheRateAtUnity_AndSplicesNothing()
    {
        var buffer = SignalBuffer.Constant(0.4f);
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        buffer.IsHardSyncPending = true;
        provider.SetResampling(1.004);
        source.Read(new float[CallbackSamples], 0, CallbackSamples);

        Assert.Equal(1.0, source.PlaybackRate);
        Assert.Equal(1.0, buffer.LastReportedRate);
        Assert.Equal(0, buffer.SamplesDroppedReported);
        Assert.Equal(0, buffer.SamplesInsertedReported);
    }

    /// <summary>
    /// The other half of moving the gate to the actor: a provider <em>predicting</em>
    /// <see cref="SyncCorrectionMode.HardSync"/> is not a reason to stand down. The buffer
    /// declines to snap on a sign disagreement, past the re-anchor ceiling and inside its grace
    /// windows, and while it is declining the drift is this source's to correct — standing
    /// down on the forecast left it uncorrected with nobody acting at all.
    /// </summary>
    [Fact]
    public void PredictedHardSync_WithNoSnapInFlight_StillCorrects()
    {
        var buffer = SignalBuffer.Constant(0.4f);
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        provider.SetHardSync(rate: 1.004);
        source.Read(new float[CallbackSamples], 0, CallbackSamples);

        Assert.Equal(1.004, source.PlaybackRate, 9);
        Assert.Equal(1.004, buffer.LastReportedRate, 9);
    }

    // ── The correction actually lands ───────────────────────────────────────

    /// <summary>
    /// A speed-up is only a correction if it consumes content faster than it emits it. Over a
    /// long run at 1.005 the chain must read about 0.5% more input frames than it outputs;
    /// a chain that pads its shortfalls with repeated frames would tick the counter and cancel the
    /// correction at the same time, and this is what catches that.
    /// </summary>
    [Theory]
    [InlineData(1.005)]
    [InlineData(0.995)]
    public void RateCorrection_ConsumesInputInProportionToTheRate(double rate)
    {
        var buffer = SignalBuffer.Sine(220.0, 0.5);
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        provider.SetResampling(rate);

        var output = new float[CallbackSamples];
        const int callbacks = 500;
        for (var cb = 0; cb < callbacks; cb++)
        {
            source.Read(output, 0, CallbackSamples);
        }

        var outputFrames = (long)callbacks * (CallbackSamples / Channels);
        var actualRatio = (double)buffer.FramesDelivered / outputFrames;

        // A couple of frames of resampler priming over 500 callbacks is well inside 0.05%.
        Assert.InRange(actualRatio, rate - 0.0005, rate + 0.0005);

        // And it got there without leaning on concealment: a chain that is chronically a frame
        // short every callback would also satisfy the ratio above while hiding a steady artefact.
        Assert.Equal(0, source.ConcealedFrameCount);
    }

    // ── The drop/insert fallback, through the same composition ──────────────

    /// <summary>
    /// <see cref="SyncCorrectionMechanism.FrameStepping"/> takes the resampler out of the chain
    /// entirely and corrects by splicing whole frames instead. The buffer's accounting has to stay
    /// truthful across the switch, so the drops are reported through
    /// <see cref="ITimedAudioBuffer.NotifyExternalCorrection"/>, and the block still comes back
    /// full — a per-callback shortfall of exactly the corrections applied is how the silence tail
    /// in defect 2 was manufactured in the first place.
    /// </summary>
    [Fact]
    public void FrameStepping_Dropping_ReportsCorrectionsAndFillsTheBlock()
    {
        const int dropEveryN = 200;
        var buffer = SignalBuffer.Sine(220.0, 0.5, FrameSteppingOptions());
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        provider.SetDropping(dropEveryN);

        var output = new float[CallbackSamples];
        const int callbacks = 100;
        for (var cb = 0; cb < callbacks; cb++)
        {
            var real = source.Read(output, 0, CallbackSamples);
            Assert.Equal(CallbackSamples, real);
        }

        var outputFrames = (long)callbacks * (CallbackSamples / Channels);
        var expectedDrops = outputFrames / dropEveryN;

        Assert.Equal(expectedDrops * Channels, buffer.SamplesDroppedReported);
        Assert.Equal(0, buffer.SamplesInsertedReported);

        // Dropping consumes one extra frame per correction: the read cursor gains on the clock.
        Assert.Equal(outputFrames + expectedDrops, buffer.FramesDelivered);
    }

    /// <summary>
    /// The mirror case. Inserting emits a frame without consuming one, so the buffer is asked for
    /// correspondingly less and the cursor gives ground back to the clock.
    /// </summary>
    [Fact]
    public void FrameStepping_Inserting_ReportsCorrectionsAndFillsTheBlock()
    {
        const int insertEveryN = 200;
        var buffer = SignalBuffer.Sine(220.0, 0.5, FrameSteppingOptions());
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        provider.SetInserting(insertEveryN);

        var output = new float[CallbackSamples];
        const int callbacks = 100;
        for (var cb = 0; cb < callbacks; cb++)
        {
            var real = source.Read(output, 0, CallbackSamples);
            Assert.Equal(CallbackSamples, real);
        }

        var outputFrames = (long)callbacks * (CallbackSamples / Channels);
        var expectedInserts = outputFrames / insertEveryN;

        Assert.Equal(expectedInserts * Channels, buffer.SamplesInsertedReported);
        Assert.Equal(0, buffer.SamplesDroppedReported);
        Assert.Equal(outputFrames - expectedInserts, buffer.FramesDelivered);
    }

    /// <summary>
    /// A splice is a blend, not a cut: three-point weighted interpolation keeps the waveform's
    /// slope continuous where a raw discard or repeat would leave a step. The bound is generous —
    /// a spliced frame is a genuine, if small, deviation — but a cut at 220 Hz would step by far
    /// more than the sine's own slope allows.
    /// </summary>
    [Fact]
    public void FrameStepping_SplicesWithoutStepDiscontinuities()
    {
        const double frequency = 220.0;
        const double amplitude = 0.5;

        var buffer = SignalBuffer.Sine(frequency, amplitude, FrameSteppingOptions());
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        provider.SetDropping(200);

        var maxDelta = MeasureMaxSampleDelta(source, callbacks: 200, rateForCallback: null, provider);
        var slopeBound = amplitude * 2 * Math.PI * frequency / SampleRate;

        Assert.True(
            maxDelta < 4 * slopeBound,
            $"splice stepped by {maxDelta:F5} (sine slope bound {slopeBound:F5})");
    }

    /// <summary>
    /// The provider emits a rate whatever mechanism the host runs, because it cannot see which
    /// one the host has: the options it was built from are its own copy, not the buffer's. It
    /// used to decide from that copy, so a mismatched pair produced a rate nothing applied.
    /// </summary>
    [Theory]
    [InlineData(SyncCorrectionMechanism.SmoothResampling)]
    [InlineData(SyncCorrectionMechanism.FrameStepping)]
    public void Calculator_ReportsARate_WhicheverMechanismTheHostRuns(SyncCorrectionMechanism mechanism)
    {
        var options = SyncCorrectionOptions.Default;
        options.Mechanism = mechanism;

        var calculator = new SyncCorrectionCalculator(options, SampleRate, Channels);
        calculator.NotifySamplesProcessed(SampleRate * Channels);

        calculator.UpdateFromSyncError(2_000, 2_000);

        Assert.Equal(SyncCorrectionMode.Resampling, calculator.CurrentMode);
        Assert.InRange(calculator.TargetPlaybackRate, 1.0 + 1e-9, options.MaxRate);
    }

    /// <summary>
    /// And the host spends that rate as frame stepping when it has no resampler, at an interval
    /// whose implied speed is the rate's — one frame in N is a speed change of 1/N — and
    /// never short enough to exceed the spec's ±0.5% cap.
    /// </summary>
    [Fact]
    public void FrameStepping_SpendsTheRateAtTheEquivalentInterval()
    {
        var options = FrameSteppingOptions();
        var buffer = SignalBuffer.Sine(220.0, 0.5, options);
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        // The cap itself: the tightest interval the ladder can ask for is one frame in 200.
        provider.SetResampling(options.MaxRate);

        var output = new float[CallbackSamples];
        const int callbacks = 100;
        for (var cb = 0; cb < callbacks; cb++)
        {
            Assert.Equal(CallbackSamples, source.Read(output, 0, CallbackSamples));
        }

        var outputFrames = (long)callbacks * (CallbackSamples / Channels);
        var drops = buffer.SamplesDroppedReported / Channels;

        Assert.Equal(outputFrames / (int)Math.Ceiling(1.0 / options.EffectiveMaxSpeedCorrection), drops);
        Assert.True(
            drops / (double)outputFrames <= SyncCorrectionOptions.SpecMaxSpeedCorrection,
            "the stepping interval implies a speed past the spec's cap");
    }

    /// <summary>
    /// The default mechanism is the smooth one — this is a quality upgrade that players get by
    /// asking for the component, not by also finding a switch.
    /// </summary>
    [Fact]
    public void SmoothResamplingIsTheDefaultMechanism()
    {
        Assert.Equal(SyncCorrectionMechanism.SmoothResampling, SyncCorrectionOptions.Default.Mechanism);
        Assert.Equal(SyncCorrectionMechanism.SmoothResampling, SyncCorrectionOptions.CliDefaults.Mechanism);
        Assert.Equal(
            SyncCorrectionMechanism.FrameStepping,
            FrameSteppingOptions().Clone().Mechanism);
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>
    /// After a reset the buffer must be told the rate is neutral again. The caller owns reporting
    /// every rate change including the return to 1.0, or the stats latch on the last one seen.
    /// </summary>
    [Fact]
    public void Reset_ReturnsTheReportedRateToUnity()
    {
        var buffer = SignalBuffer.Constant(0.3f);
        var provider = new ScriptedCorrectionProvider();
        using var source = new SyncCorrectedSampleSource(buffer, () => 0, provider);

        provider.SetResampling(1.004);
        source.Read(new float[CallbackSamples], 0, CallbackSamples);
        Assert.NotEqual(1.0, buffer.LastReportedRate);

        source.Reset();

        Assert.Equal(1.0, source.PlaybackRate);
        Assert.Equal(1.0, buffer.LastReportedRate);
    }

    [Fact]
    public void ReadAfterDispose_Throws()
    {
        var buffer = SignalBuffer.Constant(0.3f);
        var source = new SyncCorrectedSampleSource(buffer, () => 0, new ScriptedCorrectionProvider());
        source.Dispose();

        Assert.Throws<ObjectDisposedException>(() => source.Read(new float[CallbackSamples], 0, CallbackSamples));
    }

    [Fact]
    public void Constructor_RejectsMissingCollaborators()
    {
        var buffer = SignalBuffer.Constant(0.3f);

        Assert.Throws<ArgumentNullException>(() => new SyncCorrectedSampleSource(null!, () => 0));
        Assert.Throws<ArgumentNullException>(() => new SyncCorrectedSampleSource(buffer, null!));
    }

    /// <summary>
    /// With no provider supplied the source builds a <see cref="SyncCorrectionCalculator"/> from
    /// the buffer's own options and format, because the policy is spec-fixed and there is nothing
    /// for a player to supply.
    /// </summary>
    [Fact]
    public void OmittedProvider_DefaultsToTheCalculator()
    {
        var buffer = SignalBuffer.Constant(0.3f);
        using var source = new SyncCorrectedSampleSource(buffer, () => 0);

        Assert.IsType<SyncCorrectionCalculator>(source.CorrectionProvider);
        Assert.Same(buffer, source.Buffer);
        Assert.Equal(Format, source.Format);
    }

    // ── Against the real buffer ─────────────────────────────────────────────

    /// <summary>
    /// End to end against <see cref="TimedAudioBuffer"/> rather than a stand-in: the source must
    /// drive a real drift down instead of merely reporting a rate. The player runs 10 ms callbacks
    /// against a wall clock it advances itself, with the producer kept ahead — no wall-clock waits,
    /// so the run is deterministic.
    /// </summary>
    [Fact]
    public void AgainstTheRealBuffer_ClosesADriftAndReportsTheRate()
    {
        var player = new RealBufferPlayer();

        // Settle past the startup grace period.
        player.Run(callbacks: 100);

        // A drift the continuous tier owns: inside the 5 ms one-shot threshold, outside the
        // 100 µs dead band. Injected by nudging the wall clock, exactly as crystal drift would.
        player.WallNow += 2_000;
        player.Run(callbacks: 20);

        var correctingRate = player.Source.PlaybackRate;
        Assert.Equal(SyncCorrectionMode.Resampling, player.Source.CorrectionProvider.CurrentMode);
        Assert.InRange(correctingRate, 1.0 + 1e-9, player.Buffer.SyncOptions.MaxRate);
        Assert.Equal(correctingRate, player.Buffer.GetStats().TargetPlaybackRate, 9);

        var errorWhileCorrecting = Math.Abs(player.Buffer.SmoothedSyncErrorMicroseconds);

        player.Run(callbacks: 1500);

        Assert.True(
            Math.Abs(player.Buffer.SmoothedSyncErrorMicroseconds) < errorWhileCorrecting,
            $"the correction did not close the drift: {errorWhileCorrecting:F0} µs → " +
            $"{Math.Abs(player.Buffer.SmoothedSyncErrorMicroseconds):F0} µs");

        // And it never left the spec's cap along the way.
        Assert.InRange(player.MaxRateSeen, player.Buffer.SyncOptions.MinRate, player.Buffer.SyncOptions.MaxRate);
        Assert.InRange(player.MinRateSeen, player.Buffer.SyncOptions.MinRate, player.Buffer.SyncOptions.MaxRate);
    }

    /// <summary>
    /// The buffer keeps the one-shot snap on the ReadRaw path, and the source must let it happen
    /// rather than fight it. A 40 ms disturbance is above the 5 ms threshold, so it resolves as a
    /// snap while the source holds the rate at 1.0.
    /// </summary>
    [Fact]
    public void AgainstTheRealBuffer_HardSyncSnapsWhileTheSourceStandsDown()
    {
        var player = new RealBufferPlayer();
        player.Run(callbacks: 100);

        var hardSyncsBefore = player.Buffer.GetStats().HardSyncCount;
        player.WallNow += 40_000;
        player.Run(callbacks: 40);

        Assert.True(
            player.Buffer.GetStats().HardSyncCount > hardSyncsBefore,
            "the buffer did not apply its one-shot snap");
        Assert.True(
            player.RateWhileHardSyncing is null or 1.0,
            $"the source corrected on top of the snap at rate {player.RateWhileHardSyncing}");
    }

    /// <summary>
    /// The external path must credit consumption exactly once, as the internal one does.
    /// </summary>
    /// <remarks>
    /// <see cref="ITimedAudioBuffer.ReadRaw"/> adds every sample it hands over to the read
    /// cursor, and the read is deliberately sized to what the splice will consume, so the
    /// correction is already accounted for before it is reported. Counting it again in
    /// <see cref="ITimedAudioBuffer.NotifyExternalCorrection"/> made the error metric converge at
    /// twice the physical correction: under steady drift the player leaves the group by about
    /// half the drift while reporting a sync error near zero.
    /// </remarks>
    [Fact]
    public void FrameStepping_AgainstTheRealBuffer_CreditsEachConsumedSampleOnce()
    {
        var provider = new ScriptedCorrectionProvider();
        var player = new RealBufferPlayer(FrameSteppingOptions(), provider);
        player.Run(callbacks: 100);

        // Gentle enough that four seconds of it stays inside the one-shot band: the snap credits
        // the cursor on its own terms and would blur what is being measured here.
        provider.SetDropping(4_800);
        player.Run(callbacks: 400);

        var stats = player.Buffer.GetStats();

        // Both counters are fed only by what actually left the ring buffer, and neither survives
        // a snap or a re-anchor intact, so those are excluded rather than compensated for.
        Assert.Equal(0, stats.HardSyncCount);
        Assert.Equal(0, stats.ReanchorCount);
        Assert.True(stats.SamplesDroppedForSync > 0, "the run never applied a correction");
        Assert.Equal(stats.TotalSamplesRead, stats.SamplesReadSinceStart);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static SyncCorrectionOptions FrameSteppingOptions()
    {
        var options = SyncCorrectionOptions.Default;
        options.Mechanism = SyncCorrectionMechanism.FrameStepping;
        return options;
    }

    /// <summary>
    /// Drives the source through <paramref name="callbacks"/> reads, optionally setting the rate
    /// per callback, and returns the largest same-channel sample-to-sample step in the output —
    /// measured across callback boundaries too, since that is where a bypass would show. The first
    /// two callbacks are excluded as resampler priming.
    /// </summary>
    private static double MeasureMaxSampleDelta(
        SyncCorrectedSampleSource source,
        int callbacks,
        Func<int, double>? rateForCallback,
        ScriptedCorrectionProvider provider)
    {
        var output = new float[CallbackSamples];
        var previous = new float[Channels];
        var maxDelta = 0.0;

        for (var cb = 0; cb < callbacks; cb++)
        {
            if (rateForCallback is not null)
            {
                provider.SetResampling(rateForCallback(cb));
            }

            source.Read(output, 0, CallbackSamples);

            if (cb >= 2)
            {
                for (var c = 0; c < Channels; c++)
                {
                    var prev = previous[c];
                    for (var i = c; i < CallbackSamples; i += Channels)
                    {
                        maxDelta = Math.Max(maxDelta, Math.Abs(output[i] - prev));
                        prev = output[i];
                    }
                }
            }

            for (var c = 0; c < Channels; c++)
            {
                previous[c] = output[CallbackSamples - Channels + c];
            }
        }

        return maxDelta;
    }

    /// <summary>
    /// A stand-in <see cref="ITimedAudioBuffer"/> that generates a known signal on demand, so the
    /// output can be checked against what the chain was given. Only the members the source touches
    /// carry behaviour; the rest satisfy the interface.
    /// </summary>
    private sealed class SignalBuffer : ITimedAudioBuffer
    {
        private readonly Func<long, float> _sampleForFrame;
        private long _frame;

        private SignalBuffer(Func<long, float> sampleForFrame, SyncCorrectionOptions options)
        {
            _sampleForFrame = sampleForFrame;
            SyncOptions = options;
        }

        /// <summary>
        /// Samples still available to hand out, as a real buffer's fill level would be. Unlimited
        /// unless a test tops it up per callback to model the buffer running dry mid-read.
        /// </summary>
        public long AvailableSamples { get; set; } = long.MaxValue;

        public long ReadRawCallCount { get; private set; }

        public long FramesDelivered { get; private set; }

        public double LastReportedRate { get; private set; } = 1.0;

        public long SamplesDroppedReported { get; private set; }

        public long SamplesInsertedReported { get; private set; }

        public AudioFormat Format => SyncCorrectedSampleSourceTests.Format;

        public SyncCorrectionOptions SyncOptions { get; }

        public double BufferedMilliseconds => 1_000;

        public double TargetBufferMilliseconds { get; set; } = 500;

        public bool IsReadyForPlayback => true;

        public long OutputLatencyMicroseconds { get; set; }

        public long CalibratedStartupLatencyMicroseconds { get; set; }

        public string? TimingSourceName { get; set; }

        public long SyncError { get; set; }

        public long SyncErrorMicroseconds => SyncError;

        public double SmoothedSyncErrorMicroseconds => SyncError;

        /// <summary>Whether the buffer is mid-snap, which is what the source stands down on.</summary>
        public bool IsHardSyncPending { get; set; }

        public static SignalBuffer Sine(double frequencyHz, double amplitude, SyncCorrectionOptions? options = null)
        {
            var increment = 2 * Math.PI * frequencyHz / SampleRate;
            return new SignalBuffer(
                frame => (float)(amplitude * Math.Sin(frame * increment)),
                options ?? SyncCorrectionOptions.Default);
        }

        public static SignalBuffer Constant(float value, SyncCorrectionOptions? options = null) =>
            new(_ => value, options ?? SyncCorrectionOptions.Default);

        public int ReadRaw(Span<float> buffer, long currentLocalTime)
        {
            ReadRawCallCount++;

            var deliver = (int)Math.Clamp(AvailableSamples, 0, buffer.Length);
            deliver -= deliver % Channels;
            AvailableSamples -= deliver;

            for (var i = 0; i < deliver; i += Channels)
            {
                var sample = _sampleForFrame(_frame++);
                for (var c = 0; c < Channels; c++)
                {
                    buffer[i + c] = sample;
                }
            }

            // The real buffer pads what it could not fill, and the source must not pass that on.
            buffer[deliver..].Clear();

            FramesDelivered += deliver / Channels;
            return deliver;
        }

        public void NotifyExternalCorrection(int samplesDropped, int samplesInserted)
        {
            SamplesDroppedReported += samplesDropped;
            SamplesInsertedReported += samplesInserted;
        }

        public void ReportExternalPlaybackRate(double rate) => LastReportedRate = rate;

        public int Read(Span<float> buffer, long currentLocalTime) => ReadRaw(buffer, currentLocalTime);

        public void Write(ReadOnlySpan<float> samples, long serverTimestamp)
        {
        }

        public void NotifyReconnect()
        {
        }

        public void Clear()
        {
        }

        public AudioBufferStats GetStats() => new() { TargetPlaybackRate = LastReportedRate };

        public void Dispose()
        {
        }

#pragma warning disable CS0618 // Obsolete members are part of the interface contract.
        public double TargetPlaybackRate => 1.0;

        public event Action<double>? TargetPlaybackRateChanged
        {
            add { }
            remove { }
        }
#pragma warning restore CS0618
    }

    /// <summary>
    /// A correction provider whose decision the test sets directly, so a rate sequence can be
    /// scripted instead of coaxed out of a feedback loop.
    /// </summary>
    private sealed class ScriptedCorrectionProvider : ISyncCorrectionProvider
    {
        public SyncCorrectionMode CurrentMode { get; private set; } = SyncCorrectionMode.None;

        public double TargetPlaybackRate { get; private set; } = 1.0;

        public event Action<ISyncCorrectionProvider>? CorrectionChanged;

        public void SetResampling(double rate)
        {
            CurrentMode = Math.Abs(rate - 1.0) < 1e-12 ? SyncCorrectionMode.None : SyncCorrectionMode.Resampling;
            TargetPlaybackRate = rate;
            CorrectionChanged?.Invoke(this);
        }

        /// <summary>
        /// Asks for the speed that one dropped frame in <paramref name="everyNFrames"/> realizes.
        /// A rate is the only currency a provider has; the interval is the source's translation
        /// of it, which is what these tests are checking.
        /// </summary>
        public void SetDropping(int everyNFrames) =>
            SetTier(SyncCorrectionMode.Dropping, 1.0 + (1.0 / everyNFrames));

        public void SetInserting(int everyNFrames) =>
            SetTier(SyncCorrectionMode.Inserting, 1.0 - (1.0 / everyNFrames));

        public void SetHardSync(double rate) => SetTier(SyncCorrectionMode.HardSync, rate);

        public void UpdateFromSyncError(long rawMicroseconds, double smoothedMicroseconds)
        {
        }

        public void Reset() => SetResampling(1.0);

        private void SetTier(SyncCorrectionMode mode, double rate)
        {
            CurrentMode = mode;
            TargetPlaybackRate = rate;
            CorrectionChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// A player driving a real <see cref="TimedAudioBuffer"/> through the source: 10 ms callbacks
    /// against a wall clock the test advances, with a producer kept a fixed distance ahead. Mirrors
    /// <c>TimedAudioBufferCorrectionTests.Player</c> so disturbances are injected the same way.
    /// </summary>
    private sealed class RealBufferPlayer
    {
        private const int ChunkMs = 20;
        private const int StepMs = 10;
        private const long ServerT0 = 1_000_000;
        private const long LocalT0 = 9_000_000_000_000;
        private const int SamplesPerMs = SampleRate * Channels / 1000;

        private readonly float[] _chunk = new float[ChunkMs * SamplesPerMs];
        private readonly float[] _callback = new float[StepMs * SamplesPerMs];
        private readonly FakeClockSynchronizer _clockSync = new();

        private long _writeServerTs = ServerT0;

        public RealBufferPlayer(
            SyncCorrectionOptions? options = null,
            ISyncCorrectionProvider? provider = null)
        {
            Buffer = new TimedAudioBuffer(Format, _clockSync, bufferCapacityMs: 5_000, options);

            // A steady tone rather than DC, so a splice would be visible if one happened.
            for (var i = 0; i < _chunk.Length; i += Channels)
            {
                var sample = (float)(0.4 * Math.Sin(i / (double)Channels * 2 * Math.PI * 220.0 / SampleRate));
                for (var c = 0; c < Channels; c++)
                {
                    _chunk[i + c] = sample;
                }
            }

            _clockSync.OffsetMicroseconds = ServerT0 - LocalT0;
            _clockSync.IsConverged = true;
            _clockSync.HasMinimalSync = true;

            Source = new SyncCorrectedSampleSource(Buffer, () => WallNow, provider);

            PumpProducer();
            Read();
        }

        public TimedAudioBuffer Buffer { get; }

        public SyncCorrectedSampleSource Source { get; }

        public long WallNow { get; set; } = LocalT0;

        public double MaxRateSeen { get; private set; } = 1.0;

        public double MinRateSeen { get; private set; } = 1.0;

        public double? RateWhileHardSyncing { get; private set; }

        public void Run(int callbacks)
        {
            for (var i = 0; i < callbacks; i++)
            {
                WallNow += StepMs * 1000L;
                PumpProducer();
                Read();
            }
        }

        private void PumpProducer()
        {
            var serverNow = WallNow + _clockSync.OffsetMicroseconds;
            while (_writeServerTs < serverNow + 500_000)
            {
                Buffer.Write(_chunk, _writeServerTs);
                _writeServerTs += ChunkMs * 1000L;
            }
        }

        private void Read()
        {
            // Sampled before the read: the rate for a callback is settled from the snap state
            // standing when it starts, so that is the pairing the neutrality invariant is about.
            var snapInFlightAtEntry = Buffer.IsHardSyncPending;

            Source.Read(_callback, 0, _callback.Length);

            var rate = Source.PlaybackRate;
            MaxRateSeen = Math.Max(MaxRateSeen, rate);
            MinRateSeen = Math.Min(MinRateSeen, rate);

            if (snapInFlightAtEntry)
            {
                RateWhileHardSyncing = rate;
            }
        }
    }
}
