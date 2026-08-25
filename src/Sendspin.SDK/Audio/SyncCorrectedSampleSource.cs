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
/// While a snap is in flight the provider reports <see cref="SyncCorrectionMode.HardSync"/> and
/// this source holds the rate at exactly 1.0, so the two never correct the same error twice.
/// </para>
/// <para>
/// Set <see cref="SyncCorrectionOptions.Mechanism"/> to
/// <see cref="SyncCorrectionMechanism.FrameStepping"/> to fall back to discrete drop/insert; the
/// resampler is then not constructed and no audio passes through it. That is for hosts that must
/// not carry a resampler in the output chain, not a tuning choice.
/// </para>
/// <para>
/// Threading: <see cref="Read"/> is the audio-thread entry point and is not re-entrant; call it
/// from one thread at a time, as an output callback does.
/// </para>
/// </remarks>
public sealed class SyncCorrectedSampleSource : IAudioSampleSource, IDisposable
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

    private double _playbackRate = 1.0;

    /// <summary>Output frames since the last discrete drop/insert, when frame stepping.</summary>
    private int _framesSinceLastCorrection;

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

        // The rate for this callback comes from the error the previous one measured. A callback of
        // lag is nothing against a correction target measured in seconds, and settling it up front
        // keeps the rate constant for the whole block instead of stepping mid-buffer.
        ApplyCurrentRate();

        var producedFrames = _resampler is null
            ? ReadCorrectedFrames(buffer.AsSpan(offset, outputFrames * _channels), outputFrames, now)
            : ReadResampled(buffer, offset, outputFrames, now);

        return Conceal(buffer, offset, count, producedFrames * _channels);
    }

    /// <summary>
    /// Clears correction state after a buffer clear or a playback restart, so a stale rate or a
    /// half-finished drop/insert interval cannot leak into the new stream.
    /// </summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _correctionProvider.Reset();
        _resampler?.Reset();
        _framesSinceLastCorrection = 0;
        Array.Clear(_previousFrame);
        Volatile.Write(ref _playbackRate, 1.0);
        _buffer.ReportExternalPlaybackRate(1.0);
        SetResamplerRate(1.0);
    }

    /// <summary>
    /// Forwards a reconnect to the correction provider, which suppresses corrections while the
    /// clock synchronizer re-converges (see
    /// <see cref="SyncCorrectionOptions.ReconnectStabilizationMicroseconds"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="IAudioPipeline.NotifyReconnect"/> reaches the buffer and the player, not the
    /// sample source; a player holding this source should forward from its own
    /// <see cref="IAudioPlayer.NotifyReconnect"/>. Without it the provider keeps correcting
    /// against an error the re-converging clock has not finished re-measuring.
    /// </remarks>
    public void NotifyReconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _correctionProvider.NotifyReconnect();
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
    /// Reads the provider's current decision, clamps it to the spec's cap, and hands it to the
    /// resampler.
    /// </summary>
    private void ApplyCurrentRate()
    {
        var rate = 1.0;

        // HardSync is the buffer's to apply on both read paths, and it is exempt from the speed
        // cap precisely because it is a single discontinuity rather than a speed change. Correcting
        // on top of it would double-correct the same error, so the rate stays neutral for the
        // duration — enforced here rather than trusted, since a custom provider may report anything.
        if (_correctionProvider.CurrentMode != SyncCorrectionMode.HardSync)
        {
            var reported = _correctionProvider.TargetPlaybackRate;
            rate = double.IsFinite(reported)
                ? Math.Clamp(reported, _options.MinRate, _options.MaxRate)
                : 1.0;
        }

        Volatile.Write(ref _playbackRate, rate);

        // The rate is applied out here, so without this the buffer's stats would read 1.0 while the
        // audio is actively being resampled.
        _buffer.ReportExternalPlaybackRate(rate);

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
    /// There is deliberately no bypass at rate 1.0. The resampler holds buffered input and a
    /// fractional read position across calls; stepping around it strands that content and re-entry
    /// resumes from a position the stream has moved past — an audible discontinuity every time the
    /// error crosses the dead band, which is many times a minute. At an identity ratio the linear
    /// interpolation is an exact passthrough anyway (each output frame reads at fraction 0.0), so
    /// the bypass would buy nothing but the click. See
    /// <c>DeadbandSteadyState_IsBitIdenticalPassthrough</c>.
    /// </para>
    /// </remarks>
    private int ReadResampled(float[] output, int offset, int outputFrames, long now)
    {
        var resampler = _resampler!;
        var totalFramesGenerated = 0;

        while (totalFramesGenerated < outputFrames)
        {
            var framesWanted = outputFrames - totalFramesGenerated;
            var framesNeeded = resampler.ResamplePrepare(
                framesWanted, _channels, out var inBuffer, out var inBufferOffset);

            // Zero means the resampler already holds enough input to make more output without
            // reading; only a non-zero request that comes back empty is a genuine stall.
            var framesRead = framesNeeded > 0
                ? ReadCorrectedFrames(
                    inBuffer.AsSpan(inBufferOffset, framesNeeded * _channels), framesNeeded, now)
                : 0;

            if (framesNeeded > 0 && framesRead == 0)
            {
                break;
            }

            var framesGenerated = resampler.ResampleOut(
                output, offset + (totalFramesGenerated * _channels), framesRead, framesWanted, _channels);

            if (framesGenerated == 0)
            {
                // No forward progress despite the read: the filter still wants lookahead it does
                // not have. Bail rather than spin; the residual is concealed by the caller.
                break;
            }

            totalFramesGenerated += framesGenerated;
        }

        return totalFramesGenerated;
    }

    /// <summary>
    /// Fills <paramref name="frames"/> frames from <see cref="ITimedAudioBuffer.ReadRaw"/>,
    /// applying discrete drop/insert when the provider asks for it, and reports what was applied
    /// back to the buffer.
    /// </summary>
    /// <returns>Frames actually produced; fewer than requested when the buffer under-delivers.</returns>
    private int ReadCorrectedFrames(Span<float> destination, int frames, long now)
    {
        var (dropEveryN, insertEveryN) = CurrentStepping();

        if (dropEveryN == 0 && insertEveryN == 0)
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
    /// Gets the discrete drop/insert intervals to apply, with the mode-neutrality rules enforced.
    /// </summary>
    private (int DropEveryN, int InsertEveryN) CurrentStepping()
    {
        // Same reasoning as the rate: the buffer owns the snap, so nothing is spliced on top of it.
        if (_correctionProvider.CurrentMode == SyncCorrectionMode.HardSync)
        {
            return (0, 0);
        }

        var dropEveryN = Math.Max(_correctionProvider.DropEveryNFrames, 0);
        var insertEveryN = Math.Max(_correctionProvider.InsertEveryNFrames, 0);

        // NotifyExternalCorrection's contract: one or the other, never both in a cycle. Dropping and
        // inserting at once is not a correction, it is two corrections cancelling, so prefer the
        // one the provider is actually asking for rather than splicing incoherently.
        if (dropEveryN > 0 && insertEveryN > 0)
        {
            insertEveryN = 0;
        }

        return (dropEveryN, insertEveryN);
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
    /// emit a 3-point weighted blend (0.25 previous, 0.5 primary, 0.25 neighbour), which keeps the
    /// waveform's slope continuous across the splice. A raw cut or repeat puts a step in the signal,
    /// and a step is a click.
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
    /// Writes one spliced frame: the 3-point blend where the input allows it, degrading to a
    /// 2-point blend and then to a straight hold as the input runs out.
    /// </summary>
    private void BlendSpliceFrame(ReadOnlySpan<float> input, int inputPos, Span<float> destination)
    {
        var frameSamples = _channels;
        var remainingInput = input.Length - inputPos;

        if (remainingInput >= frameSamples * 2)
        {
            var neighbour = inputPos + frameSamples;
            for (var i = 0; i < frameSamples; i++)
            {
                destination[i] = (0.25f * _previousFrame[i])
                    + (0.5f * input[inputPos + i])
                    + (0.25f * input[neighbour + i]);
            }

            return;
        }

        if (remainingInput >= frameSamples)
        {
            for (var i = 0; i < frameSamples; i++)
            {
                destination[i] = 0.5f * (_previousFrame[i] + input[inputPos + i]);
            }

            return;
        }

        _previousFrame.CopyTo(destination);
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
        if (producedSamples >= count)
        {
            return count;
        }

        if (producedSamples == 0)
        {
            buffer.AsSpan(offset, count).Clear();
            Interlocked.Increment(ref _underrunCount);
            LogUnderrun();
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
}
