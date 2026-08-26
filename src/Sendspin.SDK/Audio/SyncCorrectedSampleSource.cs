// <copyright file="SyncCorrectedSampleSource.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio.Resampling.ThirdParty;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Audio;

/// <summary>
/// An <see cref="IAudioSampleSource"/> that corrects sync by continuously trimming playback
/// speed through a resampler, instead of stepping whole frames. Drives
/// <see cref="ITimedAudioBuffer.ReadRaw"/> and a <see cref="ISyncCorrectionProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a quality upgrade, not a policy change.</b> The correction ladder is identical to
/// <see cref="ITimedAudioBuffer.Read"/>'s — ~100 µs dead band, the ±0.5% speed cap
/// (spec roles/player/v1.md:134), a one-shot snap above 5 ms, a re-anchor above 500 ms — and every
/// threshold is a spec constant. What differs is the <em>mechanism</em> for the continuous tier:
/// <see cref="ITimedAudioBuffer.Read"/> drops or duplicates whole frames, which is a small
/// discontinuity each time, while this source resamples the stream by up to ±0.5% and stays
/// continuous. Both stay inside the spec's cap; the resampled one is inaudible where frame
/// stepping is faintly granular. Prefer it on any device with the cycles for it.
/// </para>
/// <para>
/// Composition: <see cref="ITimedAudioBuffer.ReadRaw"/> supplies uncorrected samples and the
/// measured error, <see cref="SyncCorrectionCalculator"/> turns that error into a target rate, and
/// a vendored WDL resampler applies the rate. The buffer keeps the one-shot snap and the re-anchor
/// on both read paths, because skipping buffered content — or manufacturing silence — is a
/// timeline operation no external corrector can perform on samples it has already been handed.
/// While <see cref="ITimedAudioBuffer.IsHardSyncPending"/> is true this source holds the rate at
/// exactly 1.0 and splices nothing, so the two never correct the same error twice.
/// </para>
/// <para>
/// Set <see cref="SyncCorrectionOptions.Mechanism"/> to
/// <see cref="SyncCorrectionMechanism.FrameStepping"/> to fall back to discrete drop/insert; the
/// resampler is then not constructed and no audio passes through it. That is for hosts that must
/// not carry a resampler in the output chain, not a tuning choice. The provider is not told:
/// it emits a rate either way, and this source — the only object that knows whether a resampler
/// exists — converts that rate to a drop/insert interval of the same magnitude.
/// </para>
/// <para>
/// Threading: <see cref="Read"/> is the audio-thread entry point and is not re-entrant; call it
/// from one thread at a time, as an output callback does.
/// </para>
/// </remarks>
public sealed class SyncCorrectedSampleSource : IAudioSampleSource, IPlaybackLifecycleAware, IDisposable
{
    private readonly ITimedAudioBuffer _buffer;
    private readonly Func<long> _nowMicroseconds;
    private readonly ISyncCorrectionProvider _correctionProvider;
    private readonly SyncCorrectionOptions _options;
    private readonly ILogger _logger;
    private readonly int _channels;
    private readonly int _sampleRate;

    /// <summary>Null when <see cref="SyncCorrectionMechanism.FrameStepping"/> is selected.</summary>
    private readonly WdlResampler? _resampler;

    /// <summary>
    /// The frame most recently taken from the buffer, kept as the continuity term for the next
    /// splice so a drop or insert blends out of real content rather than out of nothing.
    /// </summary>
    private readonly float[] _previousFrame;

    /// <summary>
    /// Input frames read for a resampler region that came up short, held to be prefixed to the
    /// next one. Never handed to the resampler as a partial region — see <see cref="ReadResampled"/>.
    /// </summary>
    private float[] _carry = Array.Empty<float>();

    private int _carryFrames;

    private double _playbackRate = 1.0;

    /// <summary>Last rate handed to the buffer, so an unchanged one costs no lock.</summary>
    private double _lastReportedRate = 1.0;

    /// <summary>Output frames since the last discrete drop/insert, when frame stepping.</summary>
    private int _framesSinceLastCorrection;

    /// <summary>
    /// Set by the first callback that produced real audio. Before it, an empty callback is the
    /// buffer waiting for its scheduled start rather than starving.
    /// </summary>
    private bool _playbackStarted;

    private long _underrunCount;
    private long _concealedFrameCount;
    private long _totalSamplesDropped;
    private long _totalSamplesInserted;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncCorrectedSampleSource"/> class.
    /// </summary>
    /// <param name="buffer">The timed audio buffer to read from.</param>
    /// <param name="getCurrentTimeMicroseconds">
    /// Returns the current local time in microseconds — the same <c>Func&lt;long&gt;</c> the
    /// pipeline's <c>sourceFactory</c> hands you.
    /// </param>
    /// <param name="correctionProvider">
    /// Correction provider. When null a <see cref="SyncCorrectionCalculator"/> is built from the
    /// buffer's own <see cref="ITimedAudioBuffer.SyncOptions"/> and format, which is what nearly
    /// every player wants: the policy is spec-fixed, so there is nothing to supply.
    /// </param>
    /// <param name="logger">Optional logger for underrun and correction diagnostics.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="buffer"/> or <paramref name="getCurrentTimeMicroseconds"/> is null.
    /// </exception>
    public SyncCorrectedSampleSource(
        ITimedAudioBuffer buffer,
        Func<long> getCurrentTimeMicroseconds,
        ISyncCorrectionProvider? correctionProvider = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(getCurrentTimeMicroseconds);

        _buffer = buffer;
        _nowMicroseconds = getCurrentTimeMicroseconds;
        _logger = logger ?? NullLogger.Instance;
        _options = buffer.SyncOptions;
        _sampleRate = buffer.Format.SampleRate;
        _channels = buffer.Format.Channels;
        _previousFrame = new float[_channels];

        _correctionProvider = correctionProvider
            ?? new SyncCorrectionCalculator(_options, _sampleRate, _channels, _logger);

        if (_options.Mechanism == SyncCorrectionMechanism.SmoothResampling)
        {
            _resampler = CreateResampler();
        }
    }

    /// <inheritdoc/>
    public AudioFormat Format => _buffer.Format;

    /// <summary>
    /// Gets the buffer this source reads from, so a player can reach
    /// <see cref="ITimedAudioBuffer.GetStats"/> without holding a second reference.
    /// </summary>
    public ITimedAudioBuffer Buffer => _buffer;

    /// <summary>
    /// Gets the correction provider driving this source — the one passed in, or the
    /// <see cref="SyncCorrectionCalculator"/> built for you.
    /// </summary>
    public ISyncCorrectionProvider CorrectionProvider => _correctionProvider;

    /// <summary>
    /// Gets the playback rate currently applied to the resampler. 1.0 when correction is idle,
    /// while a hard sync is in flight, or when frame stepping is selected.
    /// </summary>
    /// <remarks>
    /// Always within <see cref="SyncCorrectionOptions.MinRate"/>..<see cref="SyncCorrectionOptions.MaxRate"/>,
    /// clamped here as well as in the provider so a custom
    /// <see cref="ISyncCorrectionProvider"/> cannot push playback out of spec.
    /// </remarks>
    public double PlaybackRate => Volatile.Read(ref _playbackRate);

    /// <summary>
    /// Gets the number of callbacks that produced no buffered audio at all and were filled with
    /// silence. A count that keeps climbing during playback means the buffer is starving.
    /// </summary>
    /// <remarks>
    /// Counts only after playback has started. The empty callbacks between the output device
    /// opening and the buffer's scheduled start are expected, not starvation, and counting them
    /// made every stream start look like a stall.
    /// </remarks>
    public long UnderrunCount => Interlocked.Read(ref _underrunCount);

    /// <summary>
    /// Gets the number of frames concealed by holding the last produced frame, because the buffer
    /// under-delivered part way through a callback.
    /// </summary>
    /// <remarks>
    /// Small non-zero values are ordinary — the resampler needs a fraction of a frame more input
    /// than a whole-frame read can give it. Sustained growth means real starvation, and
    /// <see cref="UnderrunCount"/> will be climbing too.
    /// </remarks>
    public long ConcealedFrameCount => Interlocked.Read(ref _concealedFrameCount);

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Always fills <paramref name="count"/> samples — the audio thread is never handed a short
    /// block — and returns how many of them came from the buffer, matching
    /// <see cref="ITimedAudioBuffer.Read"/>'s convention. The remainder is concealed: the last
    /// produced frame is held when the shortfall interrupts otherwise-continuous audio, and only a
    /// callback that produced nothing at all is filled with silence.
    /// </para>
    /// <para>
    /// Called from the audio thread. It reads no wall clock of its own beyond the
    /// <c>Func&lt;long&gt;</c> it was given, and allocates only when discrete drop/insert is
    /// active, where it rents from <see cref="ArrayPool{T}"/> rather than allocating.
    /// </para>
    /// </remarks>
    public int Read(float[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);

        if (count <= 0)
        {
            return 0;
        }

        var outputFrames = count / _channels;
        if (outputFrames == 0)
        {
            buffer.AsSpan(offset, count).Clear();
            return 0;
        }

        var now = _nowMicroseconds();

        // The correction for this callback comes from the error the previous one measured. A
        // callback of lag is nothing against a correction target measured in seconds, and settling
        // it up front — once, from one snapshot — keeps it constant for the whole block instead of
        // stepping mid-buffer, and keeps the provider's lock out of the inner loop.
        var correction = ResolveCorrection();
        ApplyCorrection(correction);

        var producedFrames = _resampler is null
            ? ReadCorrectedFrames(buffer.AsSpan(offset, outputFrames * _channels), outputFrames, now, correction)
            : ReadResampled(buffer, offset, outputFrames, now, correction);

        return Conceal(buffer, offset, count, producedFrames * _channels);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="IAudioPipeline.Clear"/> forwards here, so a <c>stream/clear</c> reaches the
    /// resampler and the correction provider as well as the buffer.
    /// </remarks>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _correctionProvider.Reset();
        _resampler?.Reset();
        _framesSinceLastCorrection = 0;
        _carryFrames = 0;
        _playbackStarted = false;
        Array.Clear(_previousFrame);
        Volatile.Write(ref _playbackRate, 1.0);
        _buffer.ReportExternalPlaybackRate(1.0);
        _lastReportedRate = 1.0;
        SetResamplerRate(1.0);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Forwards to the correction provider. <see cref="IAudioPipeline.NotifyReconnect"/> reaches
    /// here through <see cref="IPlaybackLifecycleAware"/>; without it the provider keeps
    /// correcting against an error the re-converging clock has not finished re-measuring.
    /// </remarks>
    public void NotifyReconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _correctionProvider.NotifyReconnect();

        // Held input belongs to the pre-disconnect timeline, and the buffer abandons its own
        // in-flight snap for the same reason. Splicing it onto whatever arrives next would put a
        // step in the waveform at the one moment the clock is least able to explain it.
        _carryFrames = 0;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// There is nothing unmanaged to release; this exists as a teardown barrier. Stop the output
    /// device first, then dispose this — a <see cref="Read"/> arriving afterwards then throws
    /// rather than reading from a buffer the pipeline has already torn down.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    /// <summary>
    /// Builds the WDL resampler for identity-ratio sync correction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Linear interpolation with the IIR low-pass chain <b>off</b> (<c>filtercnt: 0</c>). That is
    /// deliberate and is half of the fix for windowsSpin issue #63's audible click.
    /// </para>
    /// <para>
    /// WDL engages the chain only while the resample ratio is off 1.0, and it never clears the
    /// filter history. This source's nominal ratio is exactly 1.0, so the ±0.5% correction makes
    /// the ratio cross unity constantly — every dead-band entry parks it at 1.0 and every exit
    /// re-engages four biquads against seconds-stale history. That is a signal-proportional
    /// broadband transient: a soft click, reported in the field and reproduced in
    /// <c>SyncCorrectedSampleSourceTests</c>.
    /// </para>
    /// <para>
    /// Nothing is lost by removing it. Anti-alias filtering only matters when downsampling folds
    /// content back below Nyquist, and at the spec's cap the ratio never leaves [0.995, 1.005]:
    /// the worst case folds content above 0.995 × Nyquist — 23.88 kHz at 48 kHz — which is not
    /// present in program material and is not audible if it were. A resampler doing genuine rate
    /// conversion would need the chain; this one does not.
    /// </para>
    /// <para>
    /// <b>Do not raise <c>filtercnt</c> here.</b> Besides the click above, WDL's output-side IIR
    /// pass — the one that runs while the ratio is below 1.0 — filters from index 0 of the output
    /// array rather than from the offset it was asked to write at. This source writes at a
    /// non-zero offset whenever the loop needs a second pass, so a non-zero <c>filtercnt</c> would
    /// filter over frames already generated in this callback and leave the new ones unfiltered.
    /// The fault is upstream and the vendored file is kept diffable against it (see its header),
    /// so the guard lives here, at the only call site that could arm it.
    /// </para>
    /// </remarks>
    private WdlResampler CreateResampler()
    {
        var resampler = new WdlResampler();
        resampler.SetMode(interp: true, filtercnt: 0, sinc: false);

        // Output-driven: ResamplePrepare is told how much output is wanted and answers with the
        // input needed for it. The alternative (`wantInputDriven: true`) answers with whatever it
        // was passed, so a caller asking for N output frames reads exactly N input frames no matter
        // what the ratio is. That is silently wrong while slowing down: at 0.995 only 99.5% of the
        // input is consumed, and the remainder accumulates inside the resampler forever — growing
        // latency, and a correction that never lands because the extra input is never played.
        // Covered by RateCorrection_ConsumesInputInProportionToTheRate.
        resampler.SetFeedMode(wantInputDriven: false);

        resampler.SetRates(_sampleRate, _sampleRate);
        return resampler;
    }

    /// <summary>
    /// Takes one snapshot of the provider's decision and turns it into what this callback will
    /// actually do — a resampler rate, or a drop/insert interval of the same magnitude.
    /// </summary>
    private CallbackCorrection ResolveCorrection()
    {
        // The one-shot snap is the buffer's on both read paths, and it is exempt from the speed
        // cap precisely because it is a single discontinuity rather than a speed change. Ask the
        // actor, not the forecast: the provider predicts HardSync from the smoothed error alone,
        // while the buffer declines to snap on a sign disagreement, past the re-anchor ceiling and
        // inside its grace windows — so the two disagree in both directions, and standing down on
        // the prediction left ordinary drift uncorrected while the buffer was doing nothing.
        if (_buffer.IsHardSyncPending)
        {
            return CallbackCorrection.Neutral;
        }

        // Clamped here as well as in the provider, so a custom one cannot take this player out of
        // spec (roles/player/v1.md:134).
        var reported = _correctionProvider.TargetPlaybackRate;
        var rate = double.IsFinite(reported)
            ? Math.Clamp(reported, _options.MinRate, _options.MaxRate)
            : 1.0;

        // The mechanism is this object's to choose: it is the only one that can see whether a
        // resampler exists. The provider's tier still gets a say in one direction — Dropping and
        // Inserting mean the error is past what SyncCorrectionOptions.ResamplingThresholdMicroseconds
        // considers worth trimming smoothly — but a rate is all it ever emits.
        var mode = _correctionProvider.CurrentMode;
        var stepping = _resampler is null
            || mode is SyncCorrectionMode.Dropping or SyncCorrectionMode.Inserting;

        if (!stepping)
        {
            return new CallbackCorrection(rate, 0, 0);
        }

        var (dropEveryN, insertEveryN) =
            SyncCorrectionPolicy.SteppingIntervalFrames(rate, _options, _channels);

        // The resampler, if there is one, stays at unity: the speed is being spent on the splices.
        return new CallbackCorrection(1.0, dropEveryN, insertEveryN);
    }

    /// <summary>
    /// Publishes the resolved rate and points the resampler at it.
    /// </summary>
    private void ApplyCorrection(in CallbackCorrection correction)
    {
        var rate = correction.ResamplerRate;
        Volatile.Write(ref _playbackRate, rate);

        // The rate is applied out here, so without this the buffer's stats would read 1.0 while the
        // audio is actively being resampled. Reported only when it moves — the buffer takes its
        // lock for every call and the rate holds still for long stretches — which still includes
        // the return to 1.0, because that is a move.
        if (rate != _lastReportedRate)
        {
            _buffer.ReportExternalPlaybackRate(rate);
            _lastReportedRate = rate;
        }

        SetResamplerRate(rate);
    }

    /// <summary>
    /// Points the resampler at the requested rate. Speeding up means asking for a lower output
    /// rate, so the same input is consumed over fewer output frames.
    /// </summary>
    private void SetResamplerRate(double rate)
    {
        if (_resampler is null)
        {
            return;
        }

        _resampler.SetRates(_sampleRate, _sampleRate / rate);
    }

    /// <summary>
    /// Runs the output-driven resampler until <paramref name="outputFrames"/> frames exist, or the
    /// buffer stops delivering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In output-driven mode one pass normally does it. The loop is for the pass that comes up
    /// short because the buffer under-delivered: it feeds what arrived, asks the resampler how much
    /// more it now needs, and tries again, so a buffer that is briefly behind costs nothing as long
    /// as it catches up within the callback. Padding a shortfall with a repeated frame instead would
    /// both tick and cancel the correction — a repeat is the opposite of consuming content faster —
    /// so nothing is ever manufactured on the input side; a residual the loop cannot fill is
    /// concealed on the output side and counted.
    /// </para>
    /// <para>
    /// <b>A partial region is never fed to the resampler.</b> WDL treats an input region shorter
    /// than the one it asked for as end-of-stream: it zero-pads to that length, resamples across
    /// the pad, and then trims the output back by a rounded estimate of the padded frames. The
    /// rounding leaks exactly one contaminated frame — a dip of <c>fracpos × signal</c>, up to
    /// ~49% — which concealment then holds for the rest of the callback. Bailing before
    /// <c>ResampleOut</c> is not an alternative either: <c>ResamplePrepare</c> does not commit, so
    /// the next call's prepare overwrites the region and the dip becomes a dropout instead. The
    /// short read is therefore carried in <see cref="_carry"/> and prefixed to the next callback's
    /// region, which is the only way the content survives intact.
    /// </para>
    /// <para>
    /// There is deliberately no bypass at rate 1.0. The resampler holds buffered input and a
    /// fractional read position across calls; stepping around it strands that content and re-entry
    /// resumes from a position the stream has moved past — an audible discontinuity every time the
    /// error crosses the dead band, which is many times a minute. At an identity ratio the read
    /// position stops advancing, so the linear interpolation settles into a fixed two-tap
    /// average: from a fresh start that fraction is 0 and the samples come through bit for bit,
    /// and after a correction it is whatever the rate left behind, which is a fixed, gentle FIR
    /// rather than anything that moves. Either way the bypass would buy nothing but the click.
    /// See <c>DeadbandSteadyState_IsBitIdenticalPassthrough</c> and
    /// <c>DeadbandAfterACorrection_StaysContinuous</c>.
    /// </para>
    /// </remarks>
    private int ReadResampled(float[] output, int offset, int outputFrames, long now, in CallbackCorrection correction)
    {
        var resampler = _resampler!;
        var totalFramesGenerated = 0;
        var framesWanted = outputFrames;
        var bufferDry = false;

        while (totalFramesGenerated < outputFrames && framesWanted > 0)
        {
            var framesNeeded = resampler.ResamplePrepare(
                framesWanted, _channels, out var inBuffer, out var inBufferOffset);

            // Zero means the resampler already holds enough input to make more output without
            // reading; only a non-zero request that comes back short is a genuine stall.
            if (framesNeeded > 0)
            {
                var framesRead = FillRegion(inBuffer, inBufferOffset, framesNeeded, now, correction, bufferDry);

                if (framesRead < framesNeeded)
                {
                    // Hold what arrived and ask for less output instead: a short region makes WDL
                    // pad, and simply abandoning the region loses the frames, because
                    // ResamplePrepare does not commit and the next prepare overwrites them. One
                    // output frame less per input frame missing always shrinks the request enough
                    // in one or two passes, the ratio being within ±0.5% of 1, and it strictly
                    // decreases, so the loop terminates.
                    StoreCarry(inBuffer, inBufferOffset, framesRead);
                    framesWanted -= framesNeeded - framesRead;
                    bufferDry = true;
                    continue;
                }
            }

            var framesGenerated = resampler.ResampleOut(
                output, offset + (totalFramesGenerated * _channels), framesNeeded, framesWanted, _channels);

            if (framesGenerated == 0)
            {
                // No forward progress despite a full region: the filter still wants lookahead it
                // does not have. Bail rather than spin; the residual is concealed by the caller.
                break;
            }

            totalFramesGenerated += framesGenerated;
            framesWanted = outputFrames - totalFramesGenerated;
        }

        return totalFramesGenerated;
    }

    /// <summary>
    /// Fills a resampler region: whatever was carried over from a short read first, then fresh
    /// content from the buffer for the remainder.
    /// </summary>
    /// <param name="region">The resampler's input region.</param>
    /// <param name="regionOffset">Where in <paramref name="region"/> the input starts.</param>
    /// <param name="frames">Frames the resampler asked for.</param>
    /// <param name="now">Current local time, for the buffer's error calculation.</param>
    /// <param name="correction">What this callback is doing about the sync error.</param>
    /// <param name="bufferDry">
    /// True once the buffer has already under-delivered in this callback, so a retry at a smaller
    /// output size fills from the carry alone. Asking a dry buffer again would count a second
    /// underrun for one stall.
    /// </param>
    /// <returns>Frames in the region; fewer than <paramref name="frames"/> when the buffer is dry.</returns>
    private int FillRegion(
        float[] region,
        int regionOffset,
        int frames,
        long now,
        in CallbackCorrection correction,
        bool bufferDry)
    {
        var carried = Math.Min(_carryFrames, frames);
        if (carried > 0)
        {
            _carry.AsSpan(0, carried * _channels)
                .CopyTo(region.AsSpan(regionOffset, carried * _channels));

            // A later region can be smaller than the one the carry was taken from (a shorter
            // callback, or a rate that lowered the demand), so keep any surplus rather than
            // dropping it on the floor.
            var surplus = _carryFrames - carried;
            if (surplus > 0)
            {
                _carry.AsSpan(carried * _channels, surplus * _channels).CopyTo(_carry);
            }

            _carryFrames = surplus;
        }

        if (carried == frames || bufferDry)
        {
            return carried;
        }

        var fresh = ReadCorrectedFrames(
            region.AsSpan(regionOffset + (carried * _channels), (frames - carried) * _channels),
            frames - carried,
            now,
            correction);

        return carried + fresh;
    }

    /// <summary>
    /// Holds an under-filled region for the next callback to complete.
    /// </summary>
    /// <remarks>
    /// The frames already carried in sit at the head of the region, so copying the whole prefix
    /// back is idempotent: a callback that adds nothing leaves the carry exactly as it was.
    /// </remarks>
    private void StoreCarry(float[] region, int regionOffset, int frames)
    {
        if (frames <= 0)
        {
            _carryFrames = 0;
            return;
        }

        var samples = frames * _channels;
        if (_carry.Length < samples)
        {
            // Off the audio thread's steady state: the region only grows when the callback size or
            // the rate does, and it settles after the first shortfall at that size.
            _carry = new float[samples];
        }

        region.AsSpan(regionOffset, samples).CopyTo(_carry);
        _carryFrames = frames;
    }

    /// <summary>
    /// Fills <paramref name="frames"/> frames from <see cref="ITimedAudioBuffer.ReadRaw"/>,
    /// applying discrete drop/insert when the provider asks for it, and reports what was applied
    /// back to the buffer.
    /// </summary>
    /// <returns>Frames actually produced; fewer than requested when the buffer under-delivers.</returns>
    private int ReadCorrectedFrames(Span<float> destination, int frames, long now, in CallbackCorrection correction)
    {
        var dropEveryN = correction.DropEveryNFrames;
        var insertEveryN = correction.InsertEveryNFrames;

        if (!correction.IsStepping)
        {
            // Nothing to splice: read straight into the destination. No copy, no rented buffer, and
            // at an identity resampler ratio the samples reach the output bit for bit.
            var read = _buffer.ReadRaw(destination, now);
            AfterRawRead(read);
            RememberLastFrame(destination, read);
            return read / _channels;
        }

        // Dropping consumes more input than it emits and inserting consumes less, so size the read
        // to what the splice will actually use. Reading a fixed block instead would either strand
        // input the buffer has already advanced past, or leave the block short by exactly the
        // number of corrections — which is how a per-callback silence tail gets manufactured.
        var corrections = (_framesSinceLastCorrection + frames) / Math.Max(dropEveryN, insertEveryN);
        var inputFrames = dropEveryN > 0 ? frames + corrections : Math.Max(frames - corrections, 0);
        var inputSamples = inputFrames * _channels;

        var rented = ArrayPool<float>.Shared.Rent(inputSamples);
        try
        {
            var read = _buffer.ReadRaw(rented.AsSpan(0, inputSamples), now);
            AfterRawRead(read);

            var (producedFrames, samplesDropped, samplesInserted) = ApplyStepping(
                rented.AsSpan(0, read), destination, frames, dropEveryN, insertEveryN);

            if (samplesDropped > 0 || samplesInserted > 0)
            {
                _buffer.NotifyExternalCorrection(samplesDropped, samplesInserted);
                _totalSamplesDropped += samplesDropped;
                _totalSamplesInserted += samplesInserted;
            }

            // No RememberLastFrame here: ApplyStepping keeps _previousFrame current as it writes.
            return producedFrames;
        }
        finally
        {
            // clearArray: false — audio data does not need zeroing, and the audio thread should not
            // pay for it.
            ArrayPool<float>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// Updates the provider from the error this read just measured, and tells it how much audio
    /// went by, so its startup grace and reconnect windows advance.
    /// </summary>
    private void AfterRawRead(int samplesRead)
    {
        _correctionProvider.UpdateFromSyncError(
            _buffer.SyncErrorMicroseconds,
            _buffer.SmoothedSyncErrorMicroseconds);

        if (samplesRead > 0 && _correctionProvider is SyncCorrectionCalculator calculator)
        {
            calculator.NotifySamplesProcessed(samplesRead);
        }
    }

    /// <summary>
    /// Copies input to output, splicing one interpolated frame every N.
    /// </summary>
    /// <remarks>
    /// A dropped frame is not simply discarded and an inserted one is not simply duplicated: both
    /// emit the weighted blend in <see cref="SpliceBlend"/>, shared with
    /// <see cref="TimedAudioBuffer"/>'s internal corrector so the two cannot drift apart.
    /// </remarks>
    /// <returns>Frames produced, and the samples dropped and inserted for the buffer's accounting.</returns>
    private (int ProducedFrames, int SamplesDropped, int SamplesInserted) ApplyStepping(
        ReadOnlySpan<float> input,
        Span<float> output,
        int outputFrames,
        int dropEveryN,
        int insertEveryN)
    {
        var frameSamples = _channels;
        var interval = Math.Max(dropEveryN, insertEveryN);
        var dropping = dropEveryN > 0;
        var outputSamples = outputFrames * frameSamples;

        var inputPos = 0;
        var outputPos = 0;
        var samplesDropped = 0;
        var samplesInserted = 0;

        while (outputPos < outputSamples)
        {
            var remainingInput = input.Length - inputPos;
            _framesSinceLastCorrection++;

            // Dropping needs two frames in hand to blend one away. Inserting needs none — it emits
            // without consuming — so it always fires when due and degrades the blend instead.
            var correctionDue = _framesSinceLastCorrection >= interval
                && (!dropping || remainingInput >= frameSamples * 2);

            if (correctionDue)
            {
                _framesSinceLastCorrection = 0;
                var spliced = output.Slice(outputPos, frameSamples);
                BlendSpliceFrame(input, inputPos, spliced);
                spliced.CopyTo(_previousFrame);

                if (dropping)
                {
                    // Two frames consumed for one emitted: the read cursor gains a frame on the clock.
                    inputPos += frameSamples * 2;
                    samplesDropped += frameSamples;
                }
                else
                {
                    // Emitted without consuming: the read cursor loses a frame to the clock.
                    samplesInserted += frameSamples;
                }

                outputPos += frameSamples;
                continue;
            }

            if (remainingInput < frameSamples)
            {
                break;
            }

            var frame = output.Slice(outputPos, frameSamples);
            input.Slice(inputPos, frameSamples).CopyTo(frame);
            frame.CopyTo(_previousFrame);

            inputPos += frameSamples;
            outputPos += frameSamples;
        }

        return (outputPos / frameSamples, samplesDropped, samplesInserted);
    }

    /// <summary>
    /// Writes one spliced frame from the input the splice point has in front of it, through the
    /// shared <see cref="SpliceBlend"/> kernel.
    /// </summary>
    private void BlendSpliceFrame(ReadOnlySpan<float> input, int inputPos, Span<float> destination)
    {
        var frameSamples = _channels;
        var remainingInput = input.Length - inputPos;

        ReadOnlySpan<float> primary = default;
        ReadOnlySpan<float> neighbour = default;

        if (remainingInput >= frameSamples)
        {
            primary = input.Slice(inputPos, frameSamples);
        }

        if (remainingInput >= frameSamples * 2)
        {
            neighbour = input.Slice(inputPos + frameSamples, frameSamples);
        }

        SpliceBlend.Blend(_previousFrame, primary, neighbour, destination);
    }

    /// <summary>
    /// Records the last frame taken from the buffer, so the first splice after a quiet stretch has
    /// a real continuity term instead of starting from silence.
    /// </summary>
    private void RememberLastFrame(ReadOnlySpan<float> produced, int producedSamples)
    {
        if (producedSamples < _channels)
        {
            return;
        }

        produced.Slice(producedSamples - _channels, _channels).CopyTo(_previousFrame);
    }

    /// <summary>
    /// Fills whatever the read did not produce, and returns the count of real buffered samples.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A shortfall part way through a callback is the ordinary case, not a catastrophe: the
    /// resampler routinely wants a fraction of a frame more input than a whole-frame read can hand
    /// it. Filling that with digital silence is what produced the reported artefact — a bit-exact
    /// zero dropped into continuous audio is a step to zero and back, i.e. a broadband click, and
    /// it fires on the order of tens of times a second under continuous drift correction. Holding
    /// the last produced frame keeps the waveform continuous instead.
    /// </para>
    /// <para>
    /// A callback that produced nothing at all is the opposite case and silence is right: holding a
    /// sample across a sustained stall parks a DC offset on the speaker, which is worse than a gap
    /// and can be heard as a thump when it finally releases.
    /// </para>
    /// </remarks>
    private int Conceal(float[] buffer, int offset, int count, int producedSamples)
    {
        if (producedSamples > 0)
        {
            _playbackStarted = true;
        }

        if (producedSamples >= count)
        {
            return count;
        }

        if (producedSamples == 0)
        {
            buffer.AsSpan(offset, count).Clear();

            // Only once playback has actually started. The buffer holds its content back until
            // the scheduled start arrives while the output device is already calling, so every
            // stream start produces a run of empty callbacks that are expected, not starvation —
            // and logging them at Warning buried the stalls this counter exists to surface.
            // TimedAudioBuffer gates its own underrun counter on the same thing.
            if (_playbackStarted)
            {
                Interlocked.Increment(ref _underrunCount);
                LogUnderrun();
            }

            return 0;
        }

        var lastFrameStart = offset + producedSamples - _channels;
        for (var i = producedSamples; i < count; i++)
        {
            buffer[offset + i] = buffer[lastFrameStart + (i % _channels)];
        }

        Interlocked.Add(ref _concealedFrameCount, (count - producedSamples) / _channels);
        return producedSamples;
    }

    /// <summary>
    /// Logs a starved callback at a decaying cadence — the first few, then every power of two — so
    /// a persistent stall stays visible without flooding the log from the audio thread.
    /// </summary>
    private void LogUnderrun()
    {
        var total = Interlocked.Read(ref _underrunCount);
        if ((total & (total - 1)) != 0)
        {
            return;
        }

        _logger.LogWarning(
            "[Correction] Sample source starved: {Underruns} empty callbacks, {Concealed} frames concealed, " +
            "rate={Rate:F5}x mode={Mode} dropped={Dropped} inserted={Inserted}",
            total,
            Interlocked.Read(ref _concealedFrameCount),
            Volatile.Read(ref _playbackRate),
            _correctionProvider.CurrentMode,
            _totalSamplesDropped,
            _totalSamplesInserted);
    }

    /// <summary>
    /// What one callback will do about the sync error: the resolved rate, already translated into
    /// whichever currency this source can actually spend.
    /// </summary>
    /// <param name="ResamplerRate">
    /// Rate for the resampler. Exactly 1.0 when there is no resampler, when the speed is being
    /// spent on splices instead, and while the buffer's one-shot snap is in flight.
    /// </param>
    /// <param name="DropEveryNFrames">Drop one frame every N; 0 when not dropping.</param>
    /// <param name="InsertEveryNFrames">Insert one frame every N; 0 when not inserting.</param>
    private readonly record struct CallbackCorrection(
        double ResamplerRate,
        int DropEveryNFrames,
        int InsertEveryNFrames)
    {
        /// <summary>Gets the do-nothing correction: unity rate, no splices.</summary>
        internal static CallbackCorrection Neutral { get; } = new(1.0, 0, 0);

        /// <summary>
        /// Gets whether this callback splices frames. Never both directions at once:
        /// <see cref="SyncCorrectionPolicy.SteppingIntervalFrames"/> returns one or the other,
        /// because dropping and inserting together is two corrections cancelling.
        /// </summary>
        internal bool IsStepping => DropEveryNFrames > 0 || InsertEveryNFrames > 0;
    }
}
