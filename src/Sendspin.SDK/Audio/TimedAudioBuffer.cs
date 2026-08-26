// <copyright file="TimedAudioBuffer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Audio;

/// <summary>
/// Thread-safe circular buffer that releases audio at the correct time based on server timestamps.
/// Uses IClockSynchronizer to convert server timestamps to local playback times.
/// </summary>
/// <remarks>
/// <para>
/// This buffer implements a producer-consumer pattern where:
/// - The WebSocket receive thread writes decoded audio with server timestamps
/// - The NAudio audio thread reads samples when their playback time arrives
/// </para>
/// <para>
/// Timing strategy:
/// - Each write associates samples with a server timestamp (when they should play)
/// - On read, we check if the oldest segment's playback time has arrived
/// - If not ready, we output silence (prevents playing audio too early)
/// - If past due, we play immediately (catches up on delayed audio)
/// </para>
/// </remarks>
public sealed class TimedAudioBuffer : ITimedAudioBuffer
{
    private readonly ILogger<TimedAudioBuffer> _logger;
    private readonly IClockSynchronizer _clockSync;
    private readonly SyncCorrectionOptions _syncOptions;
    private readonly object _lock = new();

    // Rate limiting for underrun/overrun logging (microseconds)
    private const long UnderrunLogIntervalMicroseconds = 1_000_000; // Log at most once per second
    private long _lastUnderrunLogTime;
    private long _underrunsSinceLastLog;

    // Circular buffer for samples
    private float[] _buffer;
    private int _writePos;
    private int _readPos;
    private int _count;

    // Timestamp tracking - maps sample ranges to their playback times
    private readonly Queue<TimestampedSegment> _segments;
    private int _headConsumedSamples;     // Samples already consumed from the head segment
    private bool _playbackStarted;

    // Segment-timeline integrity (issue #229). Server timestamps are re-validated at every
    // segment boundary, not just before playback starts, mirroring the C++ reference's
    // per-chunk check (sync_task.cpp:596-600) and re-align (:250-344). Without this a
    // content hole — a lost chunk, a mid-play overrun drop — shifts every later sample
    // earlier in absolute time while the pace-based error reads zero, so nothing corrects it.
    private long _readCursorServerTimestamp;   // Server timestamp of the next sample to consume
    private bool _readCursorValid;
    private double _segmentGapMicroseconds;    // Content time that advanced without being output
    private long _contentHolesDetected;
    private long _lateChunksDropped;
    private long _lastTimelineLogTime;

    // Segment timestamps that tile exactly still round by up to a microsecond per chunk.
    // Anything at or below this is rounding and is absorbed silently; a real hole is at
    // least one chunk, and servers should not send chunks under 15 ms (roles/player/v1.md:153).
    private const long SegmentTimestampToleranceMicroseconds = 1_000;

    // One-shot hard sync (issue #232). Positive = skip this many samples (we are late),
    // negative = emit this many silent samples (we are early). Drains across callbacks
    // because the excess routinely exceeds one callback's worth of output.
    private long _pendingHardSyncSamples;
    private bool _hardSyncCompleted;      // Re-seed the EMA from the post-snap raw error
    private long _hardSyncCount;

    // Configuration
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _samplesPerMs;

    // Sync correction state
    private int _dropEveryNFrames;        // Drop a frame every N frames (when playing too slow)
    private int _insertEveryNFrames;      // Insert a frame every N frames (when playing too fast)
    private int _framesSinceLastCorrection; // Counter for applying corrections
    private long _samplesDroppedForSync;  // Total samples dropped for sync correction
    private long _samplesInsertedForSync; // Total samples inserted for sync correction
    private bool _needsReanchor;          // Flag to trigger re-anchoring
    private long _lastReanchorTimeMicroseconds; // Local time of last reanchor (persists across Clear)
    private long _lastReanchorCooldownLogTime;  // Rate-limit cooldown suppression logging
    private int _reanchorEventPending;    // 0 = not pending, 1 = pending (for thread-safe event coalescing)
    private float[]? _lastOutputFrame;    // Last output frame for smooth drop/insert (Python CLI approach)

    // Mode the policy last chose on the internal path, reported verbatim by GetStats. Every
    // change goes through EnterCorrectionMode, which is what logs the transition — there used
    // to be a second field tracking the same concept for the log alone, and the suppression
    // paths moved this one without it, so a correction session could end unlogged.
    private SyncCorrectionMode _correctionMode = SyncCorrectionMode.None;

    private long _correctionStartTimeUs;       // When current correction session started
    private long _droppedAtSessionStart;       // Samples dropped count at start of drop session
    private long _insertedAtSessionStart;      // Samples inserted count at start of insert session

    // Statistics
    private long _underrunCount;
    private long _overrunCount;
    private long _droppedSamples;
    private long _totalWritten;
    private long _totalRead;
    private long _reanchorCount;

    // Rolling 1-second minimum buffer depth (exposes transient dips between polls)
    private double _minBufferedMsWindow = double.MaxValue;
    private long _minWindowResetTick;
    private double _minBufferedMsRecent;
    private const long MinBufferedWindowMs = 1_000;

    // Scheduled start: when playback should begin (supports output delay feature)
    // Derived from the first segment's raw server timestamp via the CURRENT sync state
    // on every pre-start poll (includes any output delay from IClockSynchronizer).
    // We wait until this time arrives before outputting audio.
    private long _scheduledStartLocalTime;      // Target local time when playback should start (μs)

    // Sync error tracking (CLI-style: track samples READ, not samples OUTPUT)
    // Key insight: We must track samples READ from buffer, not samples OUTPUT.
    // When dropping, we read MORE than we output → samplesReadTime advances → error shrinks.
    // When inserting, we read NOTHING → samplesReadTime stays → error grows toward 0.
    private long _playbackStartLocalTime;       // Local time when playback actually started (μs)
    private long _lastElapsedMicroseconds;      // Last calculated elapsed time (for stats)
    private long _currentSyncErrorMicroseconds; // Positive = behind (need DROP), Negative = ahead (need INSERT)
    private double _smoothedSyncErrorMicroseconds; // EMA-filtered sync error for stable correction decisions

    // Self-measured startup baseline: constant read-pointer-vs-DAC plumbing offset
    // (e.g. WASAPI prefilling its output buffer at Play()) snapshot at the end of
    // the startup grace / reconnect stabilization window and subtracted thereafter.
    // Constant offsets in a pace metric are artifacts by definition (absolute
    // position is the anchor's job); genuine stalls keep growing after the
    // snapshot and still get corrected. See CaptureSyncErrorBaseline.
    private double _syncErrorBaselineMicroseconds;
    private bool _syncErrorBaselineCaptured;
    private bool _baselineDeferredLogged;

    // Post-anchor clock-drift tracking (SyncCorrectionOptions.TrackClockDrift):
    // the Kalman offset captured when the sync-error reference was (re)established,
    // and the latest drift term (current offset - anchor offset). A rising offset
    // moves the schedule earlier => playing late => positive error contribution.
    private double _clockOffsetAtAnchorUs;
    private double _clockDriftUs;
    private bool _clockOffsetCaptured;
    private long _samplesReadSinceStart;        // Total samples READ (consumed) since playback started
    private long _samplesOutputSinceStart;      // Total samples OUTPUT since playback started (for stats)
    private double _microsecondsPerSample;      // Duration of one sample in microseconds

    // Sync error smoothing (matches JS library approach)
    // EMA filter prevents jittery correction decisions from measurement noise.
    // Alpha of 0.1 means ~10 updates to reach 63% of a step change.
    // At ~10ms audio callbacks, this is ~100ms to stabilize after a change.
    private const double SyncErrorSmoothingAlpha = 0.1;

    // Reconnect stabilization: suppress corrections while Kalman filter re-converges
    private bool _inReconnectStabilization;
    private long _reconnectStabilizationStartOutput;

    private bool _disposed;

    /// <inheritdoc/>
    public AudioFormat Format { get; }

    /// <inheritdoc/>
    public SyncCorrectionOptions SyncOptions => _syncOptions.Clone();

    /// <summary>
    /// Event raised when sync error is too large and re-anchoring is needed.
    /// The pipeline should clear the buffer and restart synchronized playback.
    /// </summary>
    public event EventHandler? ReanchorRequired;

    /// <inheritdoc/>
    public double TargetBufferMilliseconds { get; set; } = 250;

    /// <inheritdoc/>
    public double MinBufferMilliseconds { get; set; } =
        PlayerBufferCapacity.DefaultMinBufferMilliseconds;

    /// <summary>
    /// Gets the buffer's decoded-audio capacity in milliseconds, as constructed.
    /// </summary>
    /// <remarks>
    /// This is the real number that <c>ClientCapabilities.BufferCapacity</c> must be derived
    /// from: the server treats the advertised byte figure as a hard limit it may fill toward
    /// (spec roles/player/v1.md:34-35), so advertising more than this holds means legally-sent
    /// audio is silently discarded. See <see cref="PlayerBufferCapacity"/>.
    /// </remarks>
    public double CapacityMilliseconds { get; }

    /// <inheritdoc/>
    public double TargetPlaybackRate { get; private set; } = 1.0;

    // Rate an external corrector (app-side resampler) reports it's applying. The internal
    // corrector uses TargetPlaybackRate; only one path is active at a time. Surfaced via GetStats.
    private double _externalPlaybackRate = 1.0;

    /// <inheritdoc/>
    public event Action<double>? TargetPlaybackRateChanged;

    /// <inheritdoc/>
    public double BufferedMilliseconds
    {
        get
        {
            lock (_lock)
            {
                return _count / (double)_samplesPerMs;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsReadyForPlayback
    {
        get
        {
            lock (_lock)
            {
                // Ready at 80% of the target depth, but never asking for more than the
                // negotiated minimum buffer: for a live stream that minimum is all the audio
                // that will ever exist before the scheduled start, so a higher gate can only
                // make the start late (issue #233).
                var required = Math.Min(TargetBufferMilliseconds * 0.8, MinBufferMilliseconds);
                return BufferedMilliseconds >= required;
            }
        }
    }

    /// <inheritdoc/>
    public long OutputLatencyMicroseconds { get; set; }

    /// <inheritdoc/>
    public long CalibratedStartupLatencyMicroseconds { get; set; }

    /// <inheritdoc/>
    public string? TimingSourceName { get; set; }

    /// <inheritdoc/>
    public long SyncErrorMicroseconds
    {
        get
        {
            lock (_lock)
            {
                return _currentSyncErrorMicroseconds;
            }
        }
    }

    /// <inheritdoc/>
    public double SmoothedSyncErrorMicroseconds
    {
        get
        {
            lock (_lock)
            {
                return _smoothedSyncErrorMicroseconds;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsHardSyncPending
    {
        get
        {
            lock (_lock)
            {
                return _pendingHardSyncSamples != 0;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TimedAudioBuffer"/> class.
    /// </summary>
    /// <param name="format">Audio format for samples.</param>
    /// <param name="clockSync">Clock synchronizer for timestamp conversion.</param>
    /// <param name="bufferCapacityMs">
    /// Decoded-audio capacity in milliseconds. This is the figure
    /// <c>ClientCapabilities.BufferCapacity</c> must be derived from — pass the same value to
    /// <c>ClientCapabilities.AudioBufferCapacityMs</c> so the server is never told it may send
    /// more than this holds. Defaults to
    /// <see cref="PlayerBufferCapacity.DefaultDecodedBufferMilliseconds"/>.
    /// </param>
    /// <param name="syncOptions">Optional sync correction options. Uses <see cref="SyncCorrectionOptions.Default"/> if not provided.</param>
    /// <param name="logger">Optional logger for diagnostics (uses NullLogger if not provided).</param>
    public TimedAudioBuffer(
        AudioFormat format,
        IClockSynchronizer clockSync,
        int bufferCapacityMs = PlayerBufferCapacity.DefaultDecodedBufferMilliseconds,
        SyncCorrectionOptions? syncOptions = null,
        ILogger<TimedAudioBuffer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(clockSync);

        _logger = logger ?? NullLogger<TimedAudioBuffer>.Instance;
        Format = format;
        _clockSync = clockSync;
        _syncOptions = syncOptions?.Clone() ?? SyncCorrectionOptions.Default;
        _syncOptions.Validate();
        SyncCorrectionPolicy.WarnIfSpeedCapExceeded(_syncOptions, _logger);
        _sampleRate = format.SampleRate;
        _channels = format.Channels;
        _samplesPerMs = (_sampleRate * _channels) / 1000;

        var bufferSamples = bufferCapacityMs * _samplesPerMs;
        _buffer = new float[bufferSamples];
        CapacityMilliseconds = bufferCapacityMs;
        _segments = new Queue<TimestampedSegment>();
        _microsecondsPerSample = 1_000_000.0 / (_sampleRate * _channels);
    }

    /// <summary>
    /// Duration of <paramref name="samples"/> interleaved samples, in microseconds.
    /// </summary>
    private long SamplesToMicroseconds(long samples) =>
        (long)Math.Round(samples * _microsecondsPerSample);

    /// <summary>
    /// Local time at which to begin emitting the sample carrying <paramref name="serverTimestamp"/>.
    /// </summary>
    /// <remarks>
    /// The clock conversion (which already applies <see cref="IClockSynchronizer.OutputDelayMs"/>) is
    /// pre-rolled by <see cref="OutputLatencyMicroseconds"/> so the sample is handed to the output that
    /// much earlier and reaches the speaker at the server's intended time. This is what keeps outputs
    /// of different latencies (each reporting its own) aligned in a multi-room group without a manual
    /// per-device offset.
    /// </remarks>
    private long ScheduledLocalTimeFor(long serverTimestamp)
        => _clockSync.ServerToClientTime(serverTimestamp) - OutputLatencyMicroseconds;

    /// <summary>
    /// Waits for the first segment's scheduled playback time, then anchors playback to it.
    /// Must be called under lock.
    /// </summary>
    /// <param name="currentLocalTime">Current local time in microseconds.</param>
    /// <param name="path">Read path name, for diagnostics.</param>
    /// <returns>False while still waiting for the scheduled start (caller emits silence).</returns>
    /// <remarks>
    /// <para>
    /// The clock conversion already has OutputDelayMs applied by
    /// <see cref="IClockSynchronizer.ServerToClientTime"/> (subtracted per spec, so a positive
    /// output delay schedules earlier). It is re-derived from the raw server timestamp on every
    /// poll rather than cached at enqueue: segments written before clock sync converged would
    /// otherwise carry a meaningless conversion and the whole pre-sync burst would look stale.
    /// </para>
    /// <para>
    /// The anchor is the <em>scheduled</em> start, not the moment the callback happened to fire
    /// (issue #233). Anchoring to "now" defines a late start as zero error — the player then
    /// trails the group for the whole stream while its own diagnostics read perfect. Anchoring
    /// to the schedule turns that lateness into an error the corrector can see, and the residual
    /// is snapped away immediately below so the first sample lands on its scheduled time.
    /// </para>
    /// </remarks>
    private bool EnsurePlaybackStarted(long currentLocalTime, string path)
    {
        if (_playbackStarted || _segments.Count == 0)
        {
            return true;
        }

        // Schedule from the read CURSOR, not from the head segment's start. After a mid-stream
        // ResetSyncTracking — every output-device switch and static-delay change takes that
        // path — the head segment is partly consumed, and anchoring to its start puts the
        // schedule one consumed prefix too early. The startup alignment then "corrects" a
        // discrepancy that does not exist, shifting the audio permanently while the reported
        // error settles back to a contented zero.
        _scheduledStartLocalTime = ScheduledLocalTimeFor(HeadCursorServerTimestamp());

        var timeUntilStart = _scheduledStartLocalTime - currentLocalTime;
        if (timeUntilStart > _syncOptions.ScheduledStartGraceWindowMicroseconds)
        {
            return false; // Not due yet — the caller emits silence and waits.
        }

        // Scheduled time is well in the past: discard the audio that can no longer be played
        // and re-derive the start from whatever is actually still due.
        if (timeUntilStart < -_syncOptions.ScheduledStartGraceWindowMicroseconds)
        {
            SkipStaleAudio(currentLocalTime);
            if (_segments.Count == 0)
            {
                return false; // Nothing left that is still due; wait for the next chunk.
            }

            _scheduledStartLocalTime = ScheduledLocalTimeFor(HeadCursorServerTimestamp());
            timeUntilStart = _scheduledStartLocalTime - currentLocalTime;
        }

        // Still further behind than the catastrophic threshold, which happens when every
        // buffered segment is stale and SkipStaleAudio has to keep the last one. Re-anchor
        // rather than start here.
        //
        // If the cooldown refuses — only reachable within a few seconds of a previous
        // re-anchor — start anyway rather than emit silence indefinitely. That is safe because
        // CaptureSyncErrorBaseline declines to absorb an error past this same threshold: the
        // lateness stays visible through the grace window and the re-anchor check takes it as
        // soon as the cooldown expires, instead of being zeroed and made permanent.
        var latenessMicroseconds = -timeUntilStart;
        if (latenessMicroseconds > _syncOptions.ReanchorThresholdMicroseconds
            && RequestReanchor(currentLocalTime))
        {
            _logger.LogWarning(
                "[Buffer] Start is {LateMs:F0}ms late, beyond the re-anchor threshold — re-anchoring",
                latenessMicroseconds / 1000.0);
            return false;
        }

        _logger.LogInformation(
            "[Buffer] Playback starting ({Path}): timeUntilStart={TimeUntilStart:F1}ms, " +
            "buffered={BufferedMs:F0}ms, segments={Segments}, scheduledStart={Scheduled}",
            path, timeUntilStart / 1000.0, _count / (double)_samplesPerMs,
            _segments.Count, _scheduledStartLocalTime);

        _playbackStarted = true;

        // Initialize sync error tracking (CLI-style: track samples READ)
        //
        // For push-model backends (ALSA), we've already consumed samples to pre-fill
        // the output buffer before playback starts. By backdating the anchor by the
        // startup latency, elapsed time matches the samples we've already read.
        //
        // sync_error = elapsedWallClock - samplesReadTime
        //   Positive = wall clock ahead = playing too slow = DROP to catch up
        //   Negative = wall clock behind = playing too fast = INSERT to slow down
        //
        // This handles static buffer fill time architecturally, so sync correction
        // only needs to handle drift and fluctuations.
        _playbackStartLocalTime = _scheduledStartLocalTime - CalibratedStartupLatencyMicroseconds;
        _samplesReadSinceStart = 0;
        _samplesOutputSinceStart = 0;

        // The content cursor restarts from the sample we are about to emit. Holes are only
        // meaningful relative to a running timeline, so anything discarded before the anchor
        // is not one.
        _readCursorServerTimestamp = HeadCursorServerTimestamp();
        _readCursorValid = true;
        _segmentGapMicroseconds = 0;

        CaptureClockOffsetReference();
        ScheduleStartupAlignment(latenessMicroseconds);
        return true;
    }

    /// <summary>
    /// Snaps the sub-grace-window residual between the scheduled start and the callback that
    /// actually started playback, so the first sample emitted is the one due now.
    /// Must be called under lock.
    /// </summary>
    /// <param name="latenessMicroseconds">
    /// How late the start is; negative when the callback ran early.
    /// </param>
    /// <remarks>
    /// The audio callback grid almost never lands on the scheduled instant, so without this
    /// every stream starts up to one grace window out and the corrector spends its first
    /// seconds grinding down a constant that a single splice removes. The spec explicitly
    /// allows a one-shot snap on startup (roles/player/v1.md:178) and the C++ reference does
    /// the same thing (silence priming when early, prefix drop when late).
    /// </remarks>
    private void ScheduleStartupAlignment(long latenessMicroseconds)
    {
        if (Math.Abs(latenessMicroseconds) > _syncOptions.ReanchorThresholdMicroseconds)
        {
            return; // Catastrophic: leave it to the re-anchor tier.
        }

        ScheduleSnap(latenessMicroseconds, "startup");
    }

    /// <summary>
    /// Schedules a one-shot snap of <paramref name="errorMicroseconds"/>, rounded to whole
    /// frames. Must be called under lock, with no snap already in flight.
    /// </summary>
    /// <param name="errorMicroseconds">
    /// How far out playback is: positive to skip that much buffered content, negative to emit
    /// that much silence.
    /// </param>
    /// <param name="reason">Which tier asked for it, for diagnostics.</param>
    /// <remarks>
    /// Both callers — <see cref="ScheduleStartupAlignment"/> and <see cref="EvaluateHardSync"/>
    /// — close the error the same way, through <see cref="ApplyPendingHardSync"/>, so both
    /// count toward <see cref="AudioBufferStats.HardSyncCount"/>. The spec requires one-shot
    /// resynchronizations to be rare (roles/player/v1.md:140), and a startup snap that did not
    /// count made that impossible to check from outside the buffer.
    /// </remarks>
    private void ScheduleSnap(long errorMicroseconds, string reason)
    {
        var samples = (long)Math.Round(errorMicroseconds / _microsecondsPerSample);
        samples -= samples % _channels; // Whole frames only.
        if (samples == 0)
        {
            return;
        }

        _pendingHardSyncSamples = samples;
        _hardSyncCount++;

        _logger.LogInformation(
            "[Correction] Hard sync #{Count} ({Reason}): {Action} {AmountMs:F1}ms in one step " +
            "(error {ErrorMs:+0.00;-0.00}ms, timing={TimingSource})",
            _hardSyncCount,
            reason,
            samples > 0 ? "skipping" : "inserting silence for",
            Math.Abs(samples) * _microsecondsPerSample / 1000.0,
            errorMicroseconds / 1000.0,
            TimingSourceName ?? "unknown");
    }

    /// <inheritdoc/>
    public void Write(ReadOnlySpan<float> samples, long serverTimestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (samples.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            // Drop chunks that arrived too late to play. The read cursor has already passed
            // their content, so enqueueing them would splice already-played audio back into
            // the timeline and shift everything after it. Spec roles/player/v1.md:145 says to
            // drop these; the C++ reference makes the same check per chunk before decoding
            // (sync_task.cpp:596-600). See SyncCorrectionOptions.LateChunkToleranceMicroseconds.
            if (_playbackStarted && _readCursorValid && IsChunkTooLate(serverTimestamp))
            {
                _lateChunksDropped++;
                LogTimelineEventIfNeeded(
                    "[Buffer] Dropped late chunk: timestamp {LateMs:F1}ms behind the read cursor (total {Total})",
                    (_readCursorServerTimestamp - serverTimestamp) / 1000.0,
                    _lateChunksDropped);
                return;
            }

            // Check for overrun
            if (_count + samples.Length > _buffer.Length)
            {
                _overrunCount++;

                if (!_playbackStarted)
                {
                    // Before playback starts, discard INCOMING audio to preserve the stream's
                    // starting position. The server's initial burst can far exceed buffer capacity
                    // (especially for compact codecs like OPUS), and dropping the oldest audio
                    // would destroy the beginning of the stream — causing the player to start
                    // from the wrong position (potentially tens of seconds into the song).
                    if (_overrunCount <= 3 || _overrunCount % 500 == 0)
                    {
                        _logger.LogDebug(
                            "[Buffer] Pre-playback overrun #{Count}: discarding incoming {ChunkMs:F1}ms to preserve stream start (buffer full at {CapacityMs}ms)",
                            _overrunCount,
                            samples.Length / (double)_samplesPerMs,
                            _buffer.Length / (double)_samplesPerMs);
                    }

                    return; // Discard incoming chunk — do NOT drop oldest
                }

                // During playback, drop oldest to make room (normal overrun behavior)
                var toDrop = (_count + samples.Length) - _buffer.Length;
                DropOldestSamples(toDrop);
                _logger.LogDebug(
                    "[Buffer] Overrun #{Count}: dropped {DroppedMs:F1}ms of oldest audio (buffer full at {CapacityMs}ms)",
                    _overrunCount,
                    toDrop / (double)_samplesPerMs,
                    _buffer.Length / (double)_samplesPerMs);
            }

            // Write samples to circular buffer
            WriteSamplesToBuffer(samples);

            // Track this segment's raw server timestamp; conversion to local time
            // happens at read time so pre-sync segments self-heal once sync converges.
            _segments.Enqueue(new TimestampedSegment(serverTimestamp, samples.Length));
            _count += samples.Length;
            _totalWritten += samples.Length;
        }
    }

    /// <inheritdoc/>
    public int Read(Span<float> buffer, long currentLocalTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            // Re-anchor first: EnsurePlaybackStarted can request one for a start too late to
            // salvage, and it returns without starting, so a check placed after the start gate
            // would never deliver it.
            if (RaiseReanchorIfPending(buffer))
            {
                return 0;
            }

            // If buffer is empty, output silence
            if (_count == 0)
            {
                if (_playbackStarted)
                {
                    _underrunCount++;
                    _underrunsSinceLastLog++;
                    LogUnderrunIfNeeded(currentLocalTime);
                }

                buffer.Fill(0f);
                return 0;
            }

            if (!EnsurePlaybackStarted(currentLocalTime, "Read"))
            {
                buffer.Fill(0f);
                return 0;
            }

            // One-shot hard sync, ahead of any continuous correction (issue #232).
            var hardSyncSilence = ApplyPendingHardSync(buffer);

            // Calculate how many samples we want to read, potentially adjusted for sync correction
            var toRead = Math.Min(buffer.Length - hardSyncSilence, _count);

            // Apply sync correction: drop or insert frames
            var (actualRead, outputCount) = ReadWithSyncCorrection(buffer.Slice(hardSyncSilence), toRead);
            outputCount += hardSyncSilence;

            _count -= actualRead;
            _totalRead += actualRead;

            // Update segment tracking
            ConsumeSegments(actualRead);

            // Update sync error tracking and correction rate (CLI-style approach)
            // IMPORTANT: Track both samplesRead AND samplesOutput separately!
            // - samplesRead advances the server cursor (what timestamp we're reading)
            // - samplesOutput advances wall clock (how much time has passed for output)
            // When dropping: read 2, output 1 → cursor advances faster → error shrinks ✓
            // When inserting: read 0, output 1 → cursor stays still → error grows toward 0 ✓
            if (_playbackStarted && outputCount > 0)
            {
                _samplesReadSinceStart += actualRead;
                _samplesOutputSinceStart += outputCount;

                CalculateSyncError(currentLocalTime);
                UpdateCorrectionRate();

                // Check if error is too large and we need to re-anchor
                // But skip this check during startup grace period
                var elapsedSinceStart = (long)(_samplesOutputSinceStart * _microsecondsPerSample);
                if (elapsedSinceStart >= _syncOptions.StartupGracePeriodMicroseconds
                    && Math.Abs(_currentSyncErrorMicroseconds) > _syncOptions.ReanchorThresholdMicroseconds)
                {
                    RequestReanchor(currentLocalTime);
                }
            }

            // Fill remainder with silence if we didn't have enough
            if (outputCount < buffer.Length)
            {
                buffer.Slice(outputCount).Fill(0f);
            }

            return outputCount;
        }
    }

    /// <inheritdoc/>
    public int ReadRaw(Span<float> buffer, long currentLocalTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            // Re-anchor first: EnsurePlaybackStarted can request one for a start too late to
            // salvage, and it returns without starting, so a check placed after the start gate
            // would never deliver it.
            if (RaiseReanchorIfPending(buffer))
            {
                return 0;
            }

            // If buffer is empty, output silence
            if (_count == 0)
            {
                if (_playbackStarted)
                {
                    _underrunCount++;
                    _underrunsSinceLastLog++;
                    LogUnderrunIfNeeded(currentLocalTime);
                }

                buffer.Fill(0f);
                return 0;
            }

            if (!EnsurePlaybackStarted(currentLocalTime, "ReadRaw"))
            {
                buffer.Fill(0f);
                return 0;
            }

            // One-shot hard sync runs on this path too. The snap is a buffer-timeline
            // operation — skipping buffered content, or manufacturing silence — that an
            // external corrector cannot perform on the samples it has already been handed,
            // and SyncCorrectionCalculator stands down while it is in flight so the two
            // never both act. See SyncCorrectionOptions.HardSyncThresholdMicroseconds.
            var hardSyncSilence = ApplyPendingHardSync(buffer);

            // Read samples directly WITHOUT continuous sync correction
            var toRead = Math.Min(buffer.Length - hardSyncSilence, _count);
            ReadSamplesFromBuffer(buffer.Slice(hardSyncSilence, toRead));

            _count -= toRead;
            _totalRead += toRead;
            ConsumeSegments(toRead);

            var outputCount = toRead + hardSyncSilence;

            // Update sync error tracking (but don't apply correction - caller does that)
            if (_playbackStarted && outputCount > 0)
            {
                _samplesReadSinceStart += toRead;
                _samplesOutputSinceStart += outputCount;

                CalculateSyncError(currentLocalTime);

                // NOTE: We do NOT call UpdateCorrectionRate() here — the caller applies
                // correction via ISyncCorrectionProvider. But we MUST still capture the
                // startup baseline, exactly as UpdateCorrectionRate does for the internal
                // path. Without it, the constant backend prefill (WASAPI gulps its full
                // ~100ms output buffer at Play()) leaks into SyncErrorMicroseconds as a
                // persistent ~-100ms error, and the external corrector grinds it out
                // forever (pinning the resampler at its max slow rate). See
                // CaptureSyncErrorBaseline.
                var elapsedSinceStart = (long)(_samplesOutputSinceStart * _microsecondsPerSample);
                if (elapsedSinceStart >= _syncOptions.StartupGracePeriodMicroseconds
                    && !_syncErrorBaselineCaptured)
                {
                    CaptureSyncErrorBaseline("startup (raw)");
                }

                // Reconnect stabilization just ended: re-capture the baseline so the re-converged
                // clock's new constant offset is absorbed rather than ground out by the external
                // corrector — the same prefill bug, relocated to the reconnect case. This mirrors
                // UpdateCorrectionRate, except we do NOT suppress corrections here: the external
                // corrector already suppresses its own during the window via
                // ISyncCorrectionProvider.NotifyReconnect.
                if (_inReconnectStabilization)
                {
                    var samplesSinceReconnect = _samplesOutputSinceStart - _reconnectStabilizationStartOutput;
                    var elapsedSinceReconnect = (long)(samplesSinceReconnect * _microsecondsPerSample);
                    if (elapsedSinceReconnect >= _syncOptions.ReconnectStabilizationMicroseconds)
                    {
                        _inReconnectStabilization = false;
                        CaptureSyncErrorBaseline("reconnect (raw)");
                        _logger.LogInformation("[Correction] Reconnect stabilization ended (raw path), baseline re-captured");
                    }
                }

                // The one-shot snap belongs to the buffer on both read paths, so evaluate it
                // here too — under the same startup-grace and reconnect suppression the
                // internal path applies in UpdateCorrectionRate, and through the same policy
                // decision. Only the snap is taken from that decision: the continuous tiers
                // are the external corrector's to apply on this path.
                if (elapsedSinceStart >= _syncOptions.StartupGracePeriodMicroseconds
                    && !_inReconnectStabilization)
                {
                    EvaluateHardSync(
                        SyncCorrectionPolicy.Decide(_smoothedSyncErrorMicroseconds, _syncOptions));
                }

                // Check re-anchor threshold
                if (elapsedSinceStart >= _syncOptions.StartupGracePeriodMicroseconds
                    && Math.Abs(_currentSyncErrorMicroseconds) > _syncOptions.ReanchorThresholdMicroseconds)
                {
                    RequestReanchor(currentLocalTime);
                }
            }

            // Fill remainder with silence if needed
            if (outputCount < buffer.Length)
            {
                buffer.Slice(outputCount).Fill(0f);
            }

            return outputCount;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Contract:</b> Either <paramref name="samplesDropped"/> OR <paramref name="samplesInserted"/>
    /// should be non-zero, but not both simultaneously. Dropping and inserting in the same correction
    /// cycle is logically invalid - you either need to speed up (drop) or slow down (insert), not both.
    /// </para>
    /// <para>
    /// A correction derived from a rate cannot violate this: a speed has one sign, so it resolves
    /// to a drop interval or an insert interval, never both. If you splice by some other rule,
    /// maintain the invariant yourself.
    /// </para>
    /// </remarks>
    public void NotifyExternalCorrection(int samplesDropped, int samplesInserted)
    {
        if (samplesDropped < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samplesDropped), samplesDropped,
                "Sample count must be non-negative.");
        }

        if (samplesInserted < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samplesInserted), samplesInserted,
                "Sample count must be non-negative.");
        }

        // Debug assertion: dropping and inserting simultaneously is logically invalid.
        // At runtime, we don't throw because SyncCorrectionCalculator already ensures
        // mutual exclusivity, and the tracking math still works (just unusual).
        System.Diagnostics.Debug.Assert(
            samplesDropped == 0 || samplesInserted == 0,
            $"NotifyExternalCorrection called with both dropped ({samplesDropped}) and inserted ({samplesInserted}) > 0. " +
            "This is logically invalid - correction should be either drop OR insert, not both.");

        lock (_lock)
        {
            // Stats only. The read cursor is already correct: ReadRaw credits every sample it
            // hands over, and a corrector must size its read to the correction — dropping needs
            // an extra frame per splice and inserting needs one fewer, and reading a fixed block
            // instead either strands content or leaves the output short by exactly the
            // corrections applied. Adjusting here as well counted the same frames twice, which
            // made the error metric converge at twice the physical correction: the reported error
            // settled near zero while the player stayed about half the drift out of the group.
            _samplesDroppedForSync += samplesDropped;
            _samplesInsertedForSync += samplesInserted;
        }
    }

    /// <inheritdoc/>
    public void ReportExternalPlaybackRate(double rate)
    {
        lock (_lock)
        {
            _externalPlaybackRate = rate;
        }
    }

    /// <inheritdoc/>
    public void NotifyReconnect()
    {
        lock (_lock)
        {
            // Reset EMA to prevent stale pre-disconnect values from polluting correction decisions
            // At α=0.1, the EMA takes ~100ms to reach 63% of a step change — without resetting,
            // old values would linger even after the stabilization period ends
            _smoothedSyncErrorMicroseconds = 0;

            _inReconnectStabilization = true;
            _reconnectStabilizationStartOutput = _samplesOutputSinceStart;

            SetNeutralCorrectionLocked();
            _framesSinceLastCorrection = 0;

            // A snap still draining was sized from a pre-disconnect error the re-converging
            // clock is about to invalidate. Abandon it for the same reason the EMA is reset;
            // whatever it had already applied is absorbed by the end-of-window baseline.
            _pendingHardSyncSamples = 0;
            _hardSyncCompleted = false;

            _logger.LogInformation("[Correction] Reconnect stabilization started (suppressing corrections for {DurationMs}ms)",
                _syncOptions.ReconnectStabilizationMicroseconds / 1000);
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_lock)
        {
            _writePos = 0;
            _readPos = 0;
            _count = 0;
            _segments.Clear();
            _headConsumedSamples = 0;

            ResetSyncStateLocked();
        }
    }

    /// <summary>
    /// Resets sync error tracking without clearing buffer content.
    /// Use this after audio device switches to prevent timing discontinuities
    /// from triggering false sync corrections.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Clear"/>, this preserves buffered audio and only resets
    /// the timing state. The next audio callback will re-anchor timing from scratch,
    /// from the read cursor rather than from the head segment's start.
    /// </remarks>
    public void ResetSyncTracking()
    {
        lock (_lock)
        {
            ResetSyncStateLocked();
        }
    }

    /// <summary>
    /// Returns every piece of timing and correction state to its post-construction value,
    /// without touching the ring or the segment queue. Must be called under lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole difference between <see cref="Clear"/> and <see cref="ResetSyncTracking"/> is
    /// whether the buffered audio survives; everything else was two copies of this list, kept
    /// in step by hand. They drifted anyway — the reconnect window's start marker was reset in
    /// one and not the other — which is the failure mode the duplication guarantees.
    /// </para>
    /// <para>
    /// Deliberately <em>not</em> reset: <see cref="_samplesDroppedForSync"/> and
    /// <see cref="_samplesInsertedForSync"/> are cumulative stats, and
    /// <see cref="_lastReanchorTimeMicroseconds"/> is the re-anchor cooldown — a re-anchor
    /// calls <see cref="Clear"/>, so resetting it here would defeat the cooldown's purpose
    /// (matching the Android and Python CLI clients).
    /// </para>
    /// </remarks>
    private void ResetSyncStateLocked()
    {
        // Playback has to re-establish its timing anchor on the next read.
        _playbackStarted = false;
        _scheduledStartLocalTime = 0;

        // Timeline state belongs to the discarded stream, not the next one. The next start
        // re-derives the content cursor from the segment it begins on, and a snap scheduled
        // against the old anchor would be measured from nothing.
        _readCursorValid = false;
        _readCursorServerTimestamp = 0;
        _segmentGapMicroseconds = 0;
        _pendingHardSyncSamples = 0;
        _hardSyncCompleted = false;

        // Sync error tracking, reset in full — as the Python CLI's clear() does for a track
        // change.
        _playbackStartLocalTime = 0;
        _lastElapsedMicroseconds = 0;
        _samplesReadSinceStart = 0;
        _samplesOutputSinceStart = 0;
        _currentSyncErrorMicroseconds = 0;
        _smoothedSyncErrorMicroseconds = 0;
        _syncErrorBaselineMicroseconds = 0;
        _syncErrorBaselineCaptured = false;
        _baselineDeferredLogged = false;
        _clockOffsetCaptured = false;
        _clockDriftUs = 0;

        // Correction state. The rate is assigned rather than set through
        // SetTargetPlaybackRate: a reset is not a correction decision, and raising the change
        // event here would ask a subscriber to re-rate audio that no longer exists.
        _correctionMode = SyncCorrectionMode.None;
        _dropEveryNFrames = 0;
        _insertEveryNFrames = 0;
        _framesSinceLastCorrection = 0;
        _needsReanchor = false;
        Interlocked.Exchange(ref _reanchorEventPending, 0);
        _lastOutputFrame = null;
        TargetPlaybackRate = 1.0;
        _externalPlaybackRate = 1.0;

        // The reconnect window's start is a marker into _samplesOutputSinceStart, zeroed just
        // above: leaving it at its old (larger) value would make samplesSinceReconnect go
        // negative and the window never close — silently suppressing corrections on the
        // internal path, and blocking the reconnect baseline re-capture on the ReadRaw one.
        _inReconnectStabilization = false;
        _reconnectStabilizationStartOutput = 0;

        // Correction session tracking, so a session that ended with the stream is not
        // reported against the next one.
        _correctionStartTimeUs = 0;
        _droppedAtSessionStart = 0;
        _insertedAtSessionStart = 0;
    }

    /// <inheritdoc/>
    public AudioBufferStats GetStats()
    {
        lock (_lock)
        {
            // Internal and external correctors are mutually exclusive (the idle one stays at 1.0),
            // so surface whichever is actually applying a rate.
            var effectivePlaybackRate = Math.Abs(_externalPlaybackRate - 1.0) > 0.0001
                ? _externalPlaybackRate
                : TargetPlaybackRate;

            // Report the mode the policy actually chose. Re-deriving it from the applied rate
            // used to hide a Resampling decision whose rate was under the 0.0001 change
            // hysteresis, so the buffer and an external corrector fed the same error could
            // disagree about what mode they were in.
            SyncCorrectionMode correctionMode;
            if (_pendingHardSyncSamples != 0)
                correctionMode = SyncCorrectionMode.HardSync;
            else if (_correctionMode != SyncCorrectionMode.None)
                correctionMode = _correctionMode;
            else if (Math.Abs(_externalPlaybackRate - 1.0) > 0.0001)
                correctionMode = SyncCorrectionMode.Resampling;
            else
                correctionMode = SyncCorrectionMode.None;

            var currentBufferedMs = _count / (double)_samplesPerMs;

            // Update rolling 1s minimum buffer depth
            var nowTick = Environment.TickCount64;
            if (_minWindowResetTick == 0 || nowTick - _minWindowResetTick >= MinBufferedWindowMs)
            {
                _minBufferedMsRecent = _minBufferedMsWindow == double.MaxValue
                    ? currentBufferedMs
                    : _minBufferedMsWindow;
                _minBufferedMsWindow = currentBufferedMs;
                _minWindowResetTick = nowTick;
            }
            else
            {
                _minBufferedMsWindow = Math.Min(_minBufferedMsWindow, currentBufferedMs);
            }

            return new AudioBufferStats
            {
                BufferedMs = currentBufferedMs,
                TargetMs = TargetBufferMilliseconds,
                UnderrunCount = _underrunCount,
                OverrunCount = _overrunCount,
                DroppedSamples = _droppedSamples,
                TotalSamplesWritten = _totalWritten,
                TotalSamplesRead = _totalRead,
                SyncErrorMicroseconds = _currentSyncErrorMicroseconds,
                SmoothedSyncErrorMicroseconds = _smoothedSyncErrorMicroseconds,
                ClockDriftMs = _clockDriftUs / 1000.0,
                IsPlaybackActive = _playbackStarted,
                SamplesDroppedForSync = _samplesDroppedForSync,
                SamplesInsertedForSync = _samplesInsertedForSync,
                CurrentCorrectionMode = correctionMode,
                TargetPlaybackRate = effectivePlaybackRate,
                SamplesReadSinceStart = _samplesReadSinceStart,
                SamplesOutputSinceStart = _samplesOutputSinceStart,
                ElapsedSinceStartMs = _lastElapsedMicroseconds / 1000.0,
                TimingSourceName = TimingSourceName,
                ReanchorCount = _reanchorCount,
                MinBufferedMsRecent = _minBufferedMsRecent,
                HardSyncCount = _hardSyncCount,
                ContentHolesDetected = _contentHolesDetected,
                LateChunksDropped = _lateChunksDropped,
            };
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_lock)
        {
            _buffer = Array.Empty<float>();
            _segments.Clear();
        }
    }

    /// <summary>
    /// Writes samples to the circular buffer.
    /// Must be called under lock.
    /// </summary>
    private void WriteSamplesToBuffer(ReadOnlySpan<float> samples)
    {
        var written = 0;
        while (written < samples.Length)
        {
            var chunkSize = Math.Min(samples.Length - written, _buffer.Length - _writePos);
            samples.Slice(written, chunkSize).CopyTo(_buffer.AsSpan(_writePos, chunkSize));
            _writePos = (_writePos + chunkSize) % _buffer.Length;
            written += chunkSize;
        }
    }

    /// <summary>
    /// Reads samples from the circular buffer.
    /// Must be called under lock.
    /// </summary>
    private int ReadSamplesFromBuffer(Span<float> buffer)
    {
        var read = 0;
        while (read < buffer.Length && read < _count)
        {
            var chunkSize = Math.Min(buffer.Length - read, _buffer.Length - _readPos);
            chunkSize = Math.Min(chunkSize, _count - read);
            _buffer.AsSpan(_readPos, chunkSize).CopyTo(buffer.Slice(read, chunkSize));
            _readPos = (_readPos + chunkSize) % _buffer.Length;
            read += chunkSize;
        }

        return read;
    }

    /// <summary>
    /// Peeks samples from the circular buffer without advancing read position.
    /// Must be called under lock.
    /// </summary>
    /// <param name="destination">Buffer to copy samples into.</param>
    /// <param name="count">Number of samples to peek.</param>
    /// <returns>Number of samples actually peeked.</returns>
    private int PeekSamplesFromBuffer(Span<float> destination, int count)
    {
        return PeekSamplesFromBufferAtOffset(destination, count, 0);
    }

    /// <summary>
    /// Peeks samples from the circular buffer at a specified offset without advancing read position.
    /// Must be called under lock.
    /// </summary>
    /// <param name="destination">Buffer to copy samples into.</param>
    /// <param name="count">Number of samples to peek.</param>
    /// <param name="offset">Offset from current read position (in samples).</param>
    /// <returns>Number of samples actually peeked.</returns>
    private int PeekSamplesFromBufferAtOffset(Span<float> destination, int count, int offset)
    {
        // Check if offset is within available data
        if (offset >= _count)
        {
            return 0;
        }

        var availableAfterOffset = _count - offset;
        var toPeek = Math.Min(count, availableAfterOffset);
        var peeked = 0;
        var tempReadPos = (_readPos + offset) % _buffer.Length;

        while (peeked < toPeek && peeked < destination.Length)
        {
            var chunkSize = Math.Min(toPeek - peeked, _buffer.Length - tempReadPos);
            chunkSize = Math.Min(chunkSize, destination.Length - peeked);
            _buffer.AsSpan(tempReadPos, chunkSize).CopyTo(destination.Slice(peeked, chunkSize));
            tempReadPos = (tempReadPos + chunkSize) % _buffer.Length;
            peeked += chunkSize;
        }

        return peeked;
    }

    /// <summary>
    /// Skips forward through the buffer to discard stale audio whose playback time has already passed.
    /// Called when playback starts and the buffer contains audio with timestamps in the past.
    /// Without this, a large buffer holding a server burst would start playing from audio that's
    /// 20+ seconds old, causing massive sync offset vs other players (even though sync error reads 0).
    /// Must be called under lock.
    /// </summary>
    /// <param name="currentLocalTime">Current local time in microseconds.</param>
    /// <returns>Number of samples skipped.</returns>
    private int SkipStaleAudio(long currentLocalTime)
    {
        var totalSkipped = 0;

        // Skip every segment whose playback time has already passed, keeping only the last
        // one so there is something to start from.
        //
        // There used to be a clamp here that stopped skipping once the buffer was down to the
        // target depth. It made the skip a no-op in exactly the case it exists for — a live
        // stream primed to just under the target — leaving the player anchored on audio that
        // was already due (issue #233). Retained audio that is stale is not a reserve; playing
        // it is the bug.
        var dueBy = currentLocalTime - _syncOptions.ScheduledStartGraceWindowMicroseconds;

        while (_segments.Count > 0 && _count > 0)
        {
            // How far past due the NEXT SAMPLE is — measured from the cursor, not from the
            // segment's start. Playback time comes from the raw server timestamp using the
            // CURRENT sync state (never a conversion cached before sync converged) and through
            // the same ScheduledLocalTimeFor the real schedule uses, so the output-latency
            // pre-roll is included here too. Comparing against the un-pre-rolled conversion
            // under-skipped by exactly the output latency.
            var staleMicroseconds = dueBy - ScheduledLocalTimeFor(HeadCursorServerTimestamp());
            if (staleMicroseconds <= 0)
            {
                break; // The next sample is still due — stop.
            }

            // Trim only the part that is actually past. A chunk whose first sample is stale
            // still has a tail that has not played yet; discarding the whole chunk throws that
            // away and leaves a hole the corrector then has to close.
            var remainingInHead = _segments.Peek().SampleCount - _headConsumedSamples;
            var staleSamples = (int)Math.Min(
                (long)(staleMicroseconds / _microsecondsPerSample),
                remainingInHead);
            staleSamples -= staleSamples % _channels;

            if (staleSamples <= 0)
            {
                break; // Less than a frame past due; nothing useful to trim.
            }

            // Keep the last segment even when all of it is stale, so there is something to
            // start from. EnsurePlaybackStarted re-anchors when that residual is catastrophic.
            if (staleSamples >= remainingInHead && _segments.Count == 1)
            {
                break;
            }

            var toSkip = Math.Min(staleSamples, _count);
            _readPos = (_readPos + toSkip) % _buffer.Length;
            _count -= toSkip;
            _droppedSamples += toSkip;
            totalSkipped += toSkip;
            ConsumeSegments(toSkip);
        }

        if (totalSkipped > 0)
        {
            _logger.LogInformation(
                "[Buffer] Skipped {SkippedMs:F0}ms of stale audio on playback start (buffer had audio from the past)",
                totalSkipped / (double)_samplesPerMs);
        }

        return totalSkipped;
    }

    /// <summary>
    /// Emits silence and fires <see cref="ReanchorRequired"/> when a re-anchor is pending.
    /// Must be called under lock.
    /// </summary>
    /// <param name="buffer">Output buffer, filled with silence when a re-anchor is pending.</param>
    /// <returns>True when a re-anchor was pending and the caller should return immediately.</returns>
    private bool RaiseReanchorIfPending(Span<float> buffer)
    {
        if (!_needsReanchor)
        {
            return false;
        }

        _needsReanchor = false;
        buffer.Fill(0f);

        // Raise the event outside the lock to prevent deadlocks. Interlocked keeps at most one
        // event in flight, so rapid reads cannot queue duplicates.
        if (Interlocked.CompareExchange(ref _reanchorEventPending, 1, 0) == 0)
        {
            try
            {
                Task.Run(() =>
                {
                    try
                    {
                        ReanchorRequired?.Invoke(this, EventArgs.Empty);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _reanchorEventPending, 0);
                    }
                });
            }
            catch
            {
                // Task.Run can throw (e.g. ThreadPool exhaustion, OutOfMemoryException).
                // Reset the pending flag so future re-anchor events are not blocked.
                Interlocked.Exchange(ref _reanchorEventPending, 0);
                throw;
            }
        }

        return true;
    }

    /// <summary>
    /// Server timestamp of the next sample that would be emitted: the head segment's start
    /// plus the prefix of it already consumed. Must be called under lock, with a non-empty
    /// segment queue.
    /// </summary>
    private long HeadCursorServerTimestamp()
        => _segments.Peek().ServerTimestamp + SamplesToMicroseconds(_headConsumedSamples);

    /// <summary>
    /// Requests a re-anchor, honouring the cooldown. Must be called under lock.
    /// </summary>
    /// <param name="currentLocalTime">Current local time in microseconds.</param>
    /// <returns>True when the re-anchor was accepted; false when the cooldown suppressed it.</returns>
    private bool RequestReanchor(long currentLocalTime)
    {
        if (currentLocalTime - _lastReanchorTimeMicroseconds < _syncOptions.ReanchorCooldownMicroseconds)
        {
            if (currentLocalTime - _lastReanchorCooldownLogTime >= UnderrunLogIntervalMicroseconds)
            {
                _lastReanchorCooldownLogTime = currentLocalTime;
                _logger.LogWarning(
                    "[Correction] Reanchor suppressed by cooldown ({CooldownMs}ms remaining)",
                    (_syncOptions.ReanchorCooldownMicroseconds - (currentLocalTime - _lastReanchorTimeMicroseconds)) / 1000);
            }

            return false;
        }

        _lastReanchorTimeMicroseconds = currentLocalTime;
        _needsReanchor = true;
        _reanchorCount++;
        return true;
    }

    /// <summary>
    /// Drops the oldest samples to make room for new data.
    /// Must be called under lock.
    /// </summary>
    private void DropOldestSamples(int toDrop)
    {
        var dropped = 0;
        while (dropped < toDrop && _count > 0)
        {
            var chunkSize = Math.Min(toDrop - dropped, _buffer.Length - _readPos);
            chunkSize = Math.Min(chunkSize, _count);
            _readPos = (_readPos + chunkSize) % _buffer.Length;
            _count -= chunkSize;
            dropped += chunkSize;
        }

        _droppedSamples += dropped;

        // Also update segment tracking
        ConsumeSegments(dropped);

        // Content discarded mid-play is a hole: everything after it now plays that much
        // early. ConsumeSegments moves the read cursor over it silently (so the next
        // segment boundary looks continuous), so the shift has to be recorded here or it
        // would never reach the sync error at all (issue #229).
        if (_playbackStarted && dropped > 0)
        {
            _segmentGapMicroseconds += SamplesToMicroseconds(dropped);
            _contentHolesDetected++;
        }
    }

    /// <summary>
    /// Consumes segment tracking entries for read/dropped samples and advances the content
    /// cursor, re-validating each segment's timestamp as it reaches the head.
    /// Must be called under lock.
    /// </summary>
    /// <remarks>
    /// A partially consumed head segment stays at the head, tracked by
    /// <see cref="_headConsumedSamples"/>. It used to be dequeued and re-enqueued, which put
    /// the remainder at the <em>tail</em> — harmless while segment timestamps were only read
    /// before playback started, and fatal now that they are the timeline.
    /// </remarks>
    private void ConsumeSegments(int samplesConsumed)
    {
        var remaining = samplesConsumed;
        while (remaining > 0 && _segments.Count > 0)
        {
            var segment = _segments.Peek();

            if (_headConsumedSamples == 0)
            {
                ObserveSegmentBoundary(segment.ServerTimestamp);
            }

            var take = Math.Min(segment.SampleCount - _headConsumedSamples, remaining);
            _headConsumedSamples += take;
            remaining -= take;

            // Recomputed from the segment base rather than accumulated, so splitting a
            // segment across callbacks cannot drift by rounding.
            _readCursorServerTimestamp =
                segment.ServerTimestamp + SamplesToMicroseconds(_headConsumedSamples);

            if (_headConsumedSamples >= segment.SampleCount)
            {
                _segments.Dequeue();
                _headConsumedSamples = 0;
            }
        }
    }

    /// <summary>
    /// Re-validates a segment's server timestamp against where the content cursor expected it,
    /// as the C++ reference does for every chunk (sync_task.cpp:596-600, 250-344).
    /// Must be called under lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A step here means the delivered timeline is not continuous: a chunk was lost in a stall,
    /// or audio was discarded. The samples after the step would otherwise play that much
    /// earlier in absolute time forever, and the pace-based error — which only compares samples
    /// consumed against wall clock — would read zero throughout, so no correction would ever
    /// fire. Folding the step into <see cref="_segmentGapMicroseconds"/> puts it in front of
    /// the corrector as what it is: the player running early by the size of the hole.
    /// </para>
    /// <para>
    /// A hole the player already sat through as silence (an underrun) cancels out: the wall
    /// clock advanced with no samples consumed, which pushes the error the other way by the
    /// same amount. That is the correct outcome — the silence filled the hole and absolute
    /// alignment was preserved.
    /// </para>
    /// </remarks>
    private void ObserveSegmentBoundary(long segmentServerTimestamp)
    {
        if (!_readCursorValid)
        {
            _readCursorServerTimestamp = segmentServerTimestamp;
            _readCursorValid = true;
            return;
        }

        var gap = segmentServerTimestamp - _readCursorServerTimestamp;
        _readCursorServerTimestamp = segmentServerTimestamp;

        if (Math.Abs(gap) <= SegmentTimestampToleranceMicroseconds || !_playbackStarted)
        {
            return;
        }

        _segmentGapMicroseconds += gap;
        _contentHolesDetected++;
        LogTimelineEventIfNeeded(
            "[Buffer] Content timeline step of {GapMs:F1}ms at a chunk boundary (total {Total}); " +
            "folding it into the sync error so alignment is restored",
            gap / 1000.0,
            _contentHolesDetected);
    }

    /// <summary>
    /// Whether a chunk arriving now is already behind the content cursor and can never play
    /// (spec roles/player/v1.md:145). Must be called under lock.
    /// </summary>
    /// <remarks>
    /// The window is <see cref="SyncCorrectionOptions.LateChunkToleranceMicroseconds"/>, which
    /// exists for this rule alone. It used to be taken from
    /// <see cref="SyncCorrectionOptions.HardSyncThresholdMicroseconds"/> — a read-side
    /// correction size — so disabling the snap tier tightened admission to the segment
    /// rounding tolerance and raising it widened admission to match.
    /// </remarks>
    private bool IsChunkTooLate(long serverTimestamp)
        => serverTimestamp < _readCursorServerTimestamp - _syncOptions.LateChunkToleranceMicroseconds;

    /// <summary>
    /// Rate-limited logging for timeline anomalies (holes, late chunks), which arrive in
    /// bursts when a network stalls. Must be called under lock.
    /// </summary>
    private void LogTimelineEventIfNeeded(string message, double magnitude, long total)
    {
        var nowTick = Environment.TickCount64 * 1000L;
        if (nowTick - _lastTimelineLogTime < UnderrunLogIntervalMicroseconds)
        {
            return;
        }

        _lastTimelineLogTime = nowTick;
        _logger.LogWarning(message, magnitude, total);
    }

    /// <summary>
    /// Calculates the current sync error using CLI-style server cursor tracking.
    /// Must be called under lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CLI approach: sync_error = expected_server_position - actual_server_cursor
    /// </para>
    /// <para>
    /// Expected position = first server timestamp + elapsed wall clock time.
    /// Actual cursor = server timestamp we've READ up to (advanced by samplesRead).
    /// </para>
    /// <para>
    /// When DROPPING (read 2, output 1):
    ///   - Cursor advances by 2 frames worth of time
    ///   - Expected advances by 1 frame worth of time (wall clock)
    ///   - Error shrinks! (cursor catches up to expected) ✓
    /// </para>
    /// <para>
    /// When INSERTING (read 0, output 1):
    ///   - Cursor stays still
    ///   - Expected advances by 1 frame worth of time (wall clock)
    ///   - Error grows toward 0! (expected catches up to cursor) ✓
    /// </para>
    /// </remarks>
    private void CalculateSyncError(long currentLocalTime)
    {
        // Elapsed wall-clock time since playback started
        var elapsedTimeMicroseconds = currentLocalTime - _playbackStartLocalTime;
        _lastElapsedMicroseconds = elapsedTimeMicroseconds;

        // How much server time have we actually READ (consumed) from the buffer?
        var samplesReadTimeMicroseconds = (long)(_samplesReadSinceStart * _microsecondsPerSample);

        // Sync error = elapsed - samples_read_time
        //
        // Positive = we haven't read enough (behind) = need to DROP (read faster)
        // Negative = we've read too much (ahead) = need to INSERT (slow down)
        //
        // Note: For push-model backends (ALSA), the static buffer pre-fill time is handled
        // by backdating _playbackStartLocalTime when playback starts (CalibratedStartup-
        // LatencyMicroseconds, an immediate seed). The self-measured baseline then trims
        // any residual constant offset (engine overhead, resampler priming, undeclared
        // prefill) so the error reflects drift/fluctuations only.
        //
        // Post-anchor server-clock movement. The pace terms above hold consumption
        // to the LOCAL clock; without this term the Kalman offset's movement since
        // anchor (relative crystal drift) accumulates as invisible absolute
        // misalignment (issue #63). Sign: offset = server - client and scheduled
        // client time is (server - offset), so a rising offset means the schedule
        // moved earlier and we are late => positive contribution.
        if (_syncOptions.TrackClockDrift)
        {
            if (!_clockOffsetCaptured)
            {
                // Deferred capture: the clock was unconverged at the last recapture
                // point; take the reference from the first converged calculation.
                CaptureClockOffsetReference();
            }
            else if (_clockSync.IsConverged)
            {
                _clockDriftUs = _clockSync.GetStatus().OffsetMicroseconds - _clockOffsetAtAnchorUs;
            }

            // Unconverged with a valid reference: hold the last term (never zero it
            // mid-flight, never follow unconverged readings).
        }

        // Content-timeline term. The pace terms above see only samples consumed versus wall
        // clock, which is blind to a break in the material itself: a lost chunk or a discard
        // makes every later sample play early by the size of the hole while those two stay in
        // perfect step. This carries the accumulated break so the corrector sees it.
        // Sign: content that advanced without being played means we are running early, i.e.
        // ahead, i.e. a negative contribution. See ObserveSegmentBoundary (issue #229).
        _currentSyncErrorMicroseconds = elapsedTimeMicroseconds - samplesReadTimeMicroseconds
            - (long)_syncErrorBaselineMicroseconds + (long)_clockDriftUs
            - (long)_segmentGapMicroseconds;

        // Apply EMA smoothing to filter measurement jitter.
        // This prevents rapid correction changes from noisy measurements while still
        // tracking the underlying trend. The smoothed value is used for correction decisions.
        //
        // Special case: if smoothed error is 0 (just started or after reset), initialize
        // it to the current raw error to avoid slow ramp-up that causes rate oscillation.
        if (_hardSyncCompleted)
        {
            // A snap just landed. The raw error already reflects it; the smoothed value still
            // carries the pre-snap magnitude and would decay through the hard-sync band for
            // another dozen callbacks, scheduling a second snap on an error that is gone.
            _hardSyncCompleted = false;
            _smoothedSyncErrorMicroseconds = _currentSyncErrorMicroseconds;
        }
        else if (_smoothedSyncErrorMicroseconds == 0 && _currentSyncErrorMicroseconds != 0)
        {
            _smoothedSyncErrorMicroseconds = _currentSyncErrorMicroseconds;
        }
        else
        {
            _smoothedSyncErrorMicroseconds = SyncErrorSmoothingAlpha * _currentSyncErrorMicroseconds
                + (1 - SyncErrorSmoothingAlpha) * _smoothedSyncErrorMicroseconds;
        }
    }

    /// <summary>
    /// Snapshots the current smoothed sync error as the constant startup baseline
    /// and rebases the error trackers to zero. Called at the end of a stabilization
    /// window (startup grace or reconnect), where corrections have been suppressed
    /// and the smoothed error has settled on whatever constant plumbing offset the
    /// output backend introduced (e.g. WASAPI's 100ms buffer prefill at Play()).
    /// Must be called under lock.
    /// </summary>
    /// <param name="reason">Window that just ended, for diagnostics.</param>
    private void CaptureSyncErrorBaseline(string reason)
    {
        // What this absorbs is a constant plumbing offset — an output backend's prefill, engine
        // overhead, resampler priming — and those are bounded. An error past the re-anchor
        // threshold is not one of them; it is misalignment the re-anchor tier owns. Absorbing
        // it here is how a catastrophically late start became permanent at a reported error of
        // zero: the startup grace suppresses the re-anchor check, and this then erased the
        // evidence before that check ever ran. Leave it visible and retry on a later callback —
        // _syncErrorBaselineCaptured stays false, so a genuine plumbing offset is still picked
        // up once the outsized error resolves (via the re-anchor, which clears and restarts).
        if (Math.Abs(_smoothedSyncErrorMicroseconds) > _syncOptions.ReanchorThresholdMicroseconds)
        {
            if (!_baselineDeferredLogged)
            {
                _baselineDeferredLogged = true;
                _logger.LogWarning(
                    "[Correction] Deferring the {Reason} sync-error baseline: {ErrorMs:F0}ms is past " +
                    "the {ThresholdMs:F0}ms re-anchor threshold, so it is misalignment rather than a " +
                    "constant offset — leaving it visible for the re-anchor tier",
                    reason,
                    _smoothedSyncErrorMicroseconds / 1000.0,
                    _syncOptions.ReanchorThresholdMicroseconds / 1000.0);
            }

            return;
        }

        // Remove the drift contribution before snapshotting: post-anchor clock
        // movement is handled by re-referencing the offset (below), not by folding
        // it into the pace baseline - otherwise the same microseconds would be
        // absorbed twice and reappear as an equal-and-opposite error once the
        // drift term resets to zero.
        _smoothedSyncErrorMicroseconds -= _clockDriftUs;
        _currentSyncErrorMicroseconds -= (long)_clockDriftUs;
        CaptureClockOffsetReference();

        var delta = _smoothedSyncErrorMicroseconds;
        _syncErrorBaselineMicroseconds += delta;
        _smoothedSyncErrorMicroseconds = 0;
        _currentSyncErrorMicroseconds -= (long)delta;
        _syncErrorBaselineCaptured = true;

        if (Math.Abs(delta) >= 1_000)
        {
            _logger.LogInformation(
                "[Correction] Captured {Reason} sync-error baseline: {BaselineMs:F1}ms (total {TotalMs:F1}ms) — constant offset will not be corrected",
                reason,
                delta / 1000.0,
                _syncErrorBaselineMicroseconds / 1000.0);
        }
    }

    /// <summary>
    /// (Re)captures the Kalman offset used as the drift reference and zeroes the
    /// drift term. Called wherever the sync-error baseline is established or
    /// absorbed, so constant offsets rebase while later movement counts as drift.
    /// When the clock is not converged, the capture is deferred to the first converged error calculation instead.
    /// Must be called under lock.
    /// </summary>
    private void CaptureClockOffsetReference()
    {
        if (_clockSync.IsConverged)
        {
            _clockOffsetAtAnchorUs = _clockSync.GetStatus().OffsetMicroseconds;
            _clockOffsetCaptured = true;
        }
        else
        {
            // Unconverged at a recapture point: defer to the first converged
            // CalculateSyncError so the convergence step becomes the reference,
            // not reported drift.
            _clockOffsetCaptured = false;
        }

        _clockDriftUs = 0;
    }

    /// <summary>
    /// Updates the correction rate based on current sync error.
    /// Must be called under lock.
    /// </summary>
    private void UpdateCorrectionRate()
    {
        // A snap in flight owns the correction: layering a rate change or frame stepping on
        // top would correct the same error twice.
        if (_pendingHardSyncSamples != 0)
        {
            SetNeutralCorrectionLocked();
            return;
        }

        // Suppress corrections during the startup grace period; initial timing
        // jitter would otherwise drive over-corrections.
        var elapsedSinceStart = (long)(_samplesOutputSinceStart * _microsecondsPerSample);
        if (elapsedSinceStart < _syncOptions.StartupGracePeriodMicroseconds)
        {
            SetNeutralCorrectionLocked();
            return;
        }

        // Grace period just ended: zero out the constant startup offset before
        // the corrector ever sees it. Without this, an undeclared backend prefill
        // (WASAPI gulps its full output buffer at Play()) reads as a persistent
        // ~-100ms error and is audibly ground out via drop/insert on every start.
        if (!_syncErrorBaselineCaptured)
        {
            CaptureSyncErrorBaseline("startup");
        }

        // Suppress corrections while the Kalman filter re-converges after reconnect.
        if (_inReconnectStabilization)
        {
            var samplesSinceReconnect = _samplesOutputSinceStart - _reconnectStabilizationStartOutput;
            var elapsedSinceReconnect = (long)(samplesSinceReconnect * _microsecondsPerSample);
            if (elapsedSinceReconnect >= _syncOptions.ReconnectStabilizationMicroseconds)
            {
                _inReconnectStabilization = false;
                CaptureSyncErrorBaseline("reconnect");
                _logger.LogInformation("[Correction] Reconnect stabilization ended, resuming corrections");
            }
            else
            {
                SetNeutralCorrectionLocked();
                return;
            }
        }

        // One decision ladder for the whole SDK — see SyncCorrectionPolicy — and one currency,
        // the rate.
        var decision = SyncCorrectionPolicy.Decide(_smoothedSyncErrorMicroseconds, _syncOptions);

        EvaluateHardSync(decision);

        // This path has no resampler, so the speed the policy chose is realized as whole-frame
        // stepping of the same magnitude: the spec's own suggested strategy (roles/player/v1.md:
        // 169-176), and what the C++ reference does per chunk. Applying the rate to nothing is
        // what once left ordinary drift to walk up to the hard-sync threshold and splice.
        var (dropEveryN, insertEveryN) = SyncCorrectionPolicy.SteppingIntervalFrames(
            decision.TargetPlaybackRate, _syncOptions, _channels);

        var mode = dropEveryN > 0
            ? SyncCorrectionMode.Dropping
            : insertEveryN > 0 ? SyncCorrectionMode.Inserting : decision.Mode;

        EnterCorrectionMode(mode);

        // Stays neutral: TargetPlaybackRate is a request to a resampler, and this path is the
        // one that has none. See ITimedAudioBuffer.TargetPlaybackRate.
        SetTargetPlaybackRate(1.0);
        _dropEveryNFrames = dropEveryN;
        _insertEveryNFrames = insertEveryN;
    }

    /// <summary>
    /// Stands the continuous corrector down: neutral rate, no frame stepping, mode
    /// <see cref="SyncCorrectionMode.None"/>. Must be called under lock.
    /// </summary>
    /// <remarks>
    /// Every path that suppresses correction — a snap in flight, the startup grace, the
    /// reconnect stabilization window, a reconnect itself — wants exactly this, and each used
    /// to spell it out. Routing them through <see cref="EnterCorrectionMode"/> also means a
    /// correction session cut short by suppression is logged as ending, which it was not.
    /// </remarks>
    private void SetNeutralCorrectionLocked()
    {
        EnterCorrectionMode(SyncCorrectionMode.None);
        SetTargetPlaybackRate(1.0);
        _dropEveryNFrames = 0;
        _insertEveryNFrames = 0;
    }

    /// <summary>
    /// Schedules a one-shot snap when <paramref name="decision"/> calls for one and none is
    /// already in flight. Must be called under lock.
    /// </summary>
    /// <param name="decision">
    /// The policy's verdict for the current smoothed error. Taking it as a parameter is what
    /// keeps both read paths on the same gate: the threshold, the re-anchor ceiling and the
    /// "tier disabled" case all live in <see cref="SyncCorrectionPolicy"/>, so neither caller
    /// can decide to snap on its own terms.
    /// </param>
    /// <remarks>
    /// Triggered on the smoothed error but sized from the raw one. The two jobs differ:
    /// deciding <em>whether</em> we are out of sync wants noise immunity, while deciding
    /// <em>how far</em> wants the freshest reading, because the splice happens at this
    /// instant. Sizing from the smoothed value instead makes the first snap deliberately
    /// short — at α=0.1 it lags a step change by an order of magnitude — so a single 60 ms
    /// disturbance becomes a 6 ms snap followed by a 54 ms one, and "rare" starts to slip.
    /// Requiring the two to agree in sign keeps a transient from splicing the wrong way; any
    /// residual is picked up on the next evaluation, as the C++ reference's settle loop does.
    /// </remarks>
    private void EvaluateHardSync(SyncCorrectionDecision decision)
    {
        if (decision.Mode != SyncCorrectionMode.HardSync)
        {
            return;
        }

        if (_pendingHardSyncSamples != 0)
        {
            return; // Already snapping; a second schedule would over-correct.
        }

        if (Math.Sign(_currentSyncErrorMicroseconds) != Math.Sign(_smoothedSyncErrorMicroseconds))
        {
            return;
        }

        // The tier gates on the smoothed error but sizes from the raw one, so after a clock
        // step the two can be an order of magnitude apart — smoothed inside the band, raw far
        // past the re-anchor ceiling. Splicing the raw figure then performs in one go exactly
        // the surgery the policy reserves for clearing the buffer. Leave it to that tier.
        if (Math.Abs(_currentSyncErrorMicroseconds) > _syncOptions.ReanchorThresholdMicroseconds)
        {
            return;
        }

        ScheduleSnap(_currentSyncErrorMicroseconds, "threshold");
    }

    /// <summary>
    /// Applies as much of a scheduled one-shot snap as this callback allows.
    /// Must be called under lock.
    /// </summary>
    /// <param name="buffer">Output buffer; silence is written at its head when inserting.</param>
    /// <returns>Samples of silence written at the head of <paramref name="buffer"/>.</returns>
    /// <remarks>
    /// Late (positive pending) skips buffered content, which advances the read cursor without
    /// producing output and closes the error immediately. Early (negative pending) emits
    /// silence, which produces output without advancing the cursor and closes the error over
    /// the duration of the silence. Either way the excess routinely exceeds one callback, so
    /// the remainder carries to the next one.
    /// </remarks>
    private int ApplyPendingHardSync(Span<float> buffer)
    {
        if (_pendingHardSyncSamples == 0)
        {
            return 0;
        }

        var frameSamples = _channels;

        if (_pendingHardSyncSamples > 0)
        {
            var toSkip = (int)Math.Min(_pendingHardSyncSamples, _count);
            toSkip -= toSkip % frameSamples;

            if (toSkip > 0)
            {
                _readPos = (_readPos + toSkip) % _buffer.Length;
                _count -= toSkip;

                // Credited as read, not as a timeline hole: this is the correction, so it must
                // move the error toward zero rather than be reported as new misalignment.
                _samplesReadSinceStart += toSkip;
                _samplesDroppedForSync += toSkip;
                ConsumeSegments(toSkip);
                _pendingHardSyncSamples -= toSkip;
            }

            if (_pendingHardSyncSamples < frameSamples)
            {
                CompleteHardSync();
            }

            return 0;
        }

        var toInsert = (int)Math.Min(-_pendingHardSyncSamples, buffer.Length);
        toInsert -= toInsert % frameSamples;

        if (toInsert > 0)
        {
            buffer.Slice(0, toInsert).Fill(0f);
            _samplesInsertedForSync += toInsert;
            _pendingHardSyncSamples += toInsert;
        }

        if (-_pendingHardSyncSamples < frameSamples)
        {
            CompleteHardSync();
        }

        return toInsert;
    }

    /// <summary>
    /// Ends the current snap and asks the next error calculation to re-seed the EMA.
    /// Must be called under lock.
    /// </summary>
    private void CompleteHardSync()
    {
        _pendingHardSyncSamples = 0;
        _hardSyncCompleted = true;
    }

    /// <summary>
    /// Moves <see cref="_correctionMode"/> to <paramref name="newMode"/>, logging the session
    /// that ended and the one that started. Must be called under lock.
    /// </summary>
    /// <remarks>
    /// The single writer of the field, so the mode <see cref="GetStats"/> reports and the mode
    /// the log describes cannot disagree. They used to be separate fields, and the suppression
    /// paths wrote only the former — so a drop/insert session cut short by the reconnect window
    /// was never logged as ending, and the next transition was reported against a session that
    /// had finished seconds earlier.
    /// </remarks>
    /// <param name="newMode">The new correction mode being entered.</param>
    private void EnterCorrectionMode(SyncCorrectionMode newMode)
    {
        if (newMode == _correctionMode)
        {
            return; // No transition
        }

        var currentTimeUs = _playbackStartLocalTime > 0
            ? _lastElapsedMicroseconds + _playbackStartLocalTime
            : 0;

        // Log the END of the previous correction session (if any)
        if (_correctionMode == SyncCorrectionMode.Dropping)
        {
            var sessionDropped = _samplesDroppedForSync - _droppedAtSessionStart;
            var sessionDurationMs = _correctionStartTimeUs > 0
                ? (currentTimeUs - _correctionStartTimeUs) / 1000.0
                : 0;

            _logger.LogInformation(
                "[Correction] Ended: DROPPING complete (dropped={DroppedSession} session, {DroppedTotal} total, duration={DurationMs:F0}ms, timing={TimingSource})",
                sessionDropped,
                _samplesDroppedForSync,
                sessionDurationMs,
                TimingSourceName ?? "unknown");
        }
        else if (_correctionMode == SyncCorrectionMode.Inserting)
        {
            var sessionInserted = _samplesInsertedForSync - _insertedAtSessionStart;
            var sessionDurationMs = _correctionStartTimeUs > 0
                ? (currentTimeUs - _correctionStartTimeUs) / 1000.0
                : 0;

            _logger.LogInformation(
                "[Correction] Ended: INSERTING complete (inserted={InsertedSession} session, {InsertedTotal} total, duration={DurationMs:F0}ms, timing={TimingSource})",
                sessionInserted,
                _samplesInsertedForSync,
                sessionDurationMs,
                TimingSourceName ?? "unknown");
        }

        // Log the START of the new correction session (if not None)
        if (newMode == SyncCorrectionMode.Dropping)
        {
            _correctionStartTimeUs = currentTimeUs;
            _droppedAtSessionStart = _samplesDroppedForSync;

            _logger.LogInformation(
                "[Correction] Started: DROPPING (syncError={SyncErrorMs:+0.00;-0.00}ms, smoothed={SmoothedMs:+0.00;-0.00}ms, " +
                "dropEveryN={DropEveryN}, elapsed={ElapsedMs:F0}ms, timing={TimingSource})",
                _currentSyncErrorMicroseconds / 1000.0,
                _smoothedSyncErrorMicroseconds / 1000.0,
                _dropEveryNFrames,
                _lastElapsedMicroseconds / 1000.0,
                TimingSourceName ?? "unknown");
        }
        else if (newMode == SyncCorrectionMode.Inserting)
        {
            _correctionStartTimeUs = currentTimeUs;
            _insertedAtSessionStart = _samplesInsertedForSync;

            _logger.LogInformation(
                "[Correction] Started: INSERTING (syncError={SyncErrorMs:+0.00;-0.00}ms, smoothed={SmoothedMs:+0.00;-0.00}ms, " +
                "insertEveryN={InsertEveryN}, elapsed={ElapsedMs:F0}ms, timing={TimingSource})",
                _currentSyncErrorMicroseconds / 1000.0,
                _smoothedSyncErrorMicroseconds / 1000.0,
                _insertEveryNFrames,
                _lastElapsedMicroseconds / 1000.0,
                TimingSourceName ?? "unknown");
        }

        _correctionMode = newMode;
    }

    /// <summary>
    /// Sets the target playback rate and raises <see cref="TargetPlaybackRateChanged"/>
    /// if the value changed. Must be called under lock.
    /// </summary>
    private void SetTargetPlaybackRate(double rate)
    {
        if (Math.Abs(TargetPlaybackRate - rate) > 0.0001)
        {
            TargetPlaybackRate = rate;
            // The event is [Obsolete] and any subscriber must be lightweight (no callback
            // back into TimedAudioBuffer). Firing under lock avoids per-rate-change allocation.
            TargetPlaybackRateChanged?.Invoke(rate);
        }
    }

    /// <summary>
    /// Reads samples with sync correction applied (drop or insert frames as needed).
    /// Must be called under lock.
    /// </summary>
    /// <param name="buffer">Output buffer to fill.</param>
    /// <param name="toRead">Number of samples to read from internal buffer.</param>
    /// <returns>Tuple of (samples consumed from buffer, samples written to output).</returns>
    /// <remarks>
    /// Uses the Python CLI approach for smoother corrections:
    /// - Drop: Read TWO frames from input, output the LAST frame (skip one input)
    /// - Insert: Output the last frame AGAIN without reading from input
    /// This maintains audio continuity by always using recently-played samples.
    /// </remarks>
    private (int ActualRead, int OutputCount) ReadWithSyncCorrection(Span<float> buffer, int toRead)
    {
        var frameSamples = _channels; // One frame = all channels for one time point

        // Initialize last output frame if needed
        _lastOutputFrame ??= new float[frameSamples];

        // If no correction needed, use optimized bulk read
        if (_dropEveryNFrames == 0 && _insertEveryNFrames == 0)
        {
            var read = ReadSamplesFromBuffer(buffer.Slice(0, toRead));

            // Save last frame for potential future corrections
            if (read >= frameSamples)
            {
                buffer.Slice(read - frameSamples, frameSamples).CopyTo(_lastOutputFrame);
            }

            return (read, read);
        }

        // Process frame by frame, applying corrections (Python CLI approach)
        var outputPos = 0;
        var samplesConsumed = 0;
        Span<float> tempFrame = stackalloc float[frameSamples];

        // Continue until output buffer is full (not until we've consumed toRead)
        // When dropping, we consume MORE from input to fill output with real audio.
        // Previously, the loop exited when samplesConsumed >= toRead, leaving the
        // output buffer partially filled with silence - which doesn't speed up playback!
        while (outputPos < buffer.Length)
        {
            // Check if we have a full frame to read from internal buffer.
            // Use _count - samplesConsumed to check ACTUAL remaining, not planned toRead.
            var remainingInBuffer = _count - samplesConsumed;
            if (remainingInBuffer < frameSamples)
            {
                break; // Underrun - not enough audio in internal buffer
            }

            // Check remaining output space
            if (buffer.Length - outputPos < frameSamples)
            {
                break;
            }

            _framesSinceLastCorrection++;

            // Check if we should DROP a frame (read two, output 3-point interpolated blend)
            if (_dropEveryNFrames > 0 && _framesSinceLastCorrection >= _dropEveryNFrames)
            {
                _framesSinceLastCorrection = 0;

                // Need at least 2 frames for interpolated drop
                if (_count - samplesConsumed >= frameSamples * 2)
                {
                    // Read frame A (the one before the drop point)
                    ReadSamplesFromBuffer(tempFrame);
                    samplesConsumed += frameSamples;

                    // Read frame B (the one we're skipping over)
                    Span<float> droppedFrame = stackalloc float[frameSamples];
                    ReadSamplesFromBuffer(droppedFrame);
                    samplesConsumed += frameSamples;

                    // The shared splice kernel: 3-point weighted interpolation of the last output,
                    // the frame at the splice, and the one being dropped.
                    var outputSpan = buffer.Slice(outputPos, frameSamples);
                    SpliceBlend.Blend(_lastOutputFrame, tempFrame, droppedFrame, outputSpan);

                    // Save interpolated frame as last output for continuity
                    outputSpan.CopyTo(_lastOutputFrame);
                    outputPos += frameSamples;
                    _samplesDroppedForSync += frameSamples;
                    continue;
                }
                else if (_count - samplesConsumed >= frameSamples)
                {
                    // Fallback: only 1 frame available, output it directly
                    ReadSamplesFromBuffer(tempFrame);
                    samplesConsumed += frameSamples;
                    tempFrame.CopyTo(buffer.Slice(outputPos, frameSamples));
                    tempFrame.CopyTo(_lastOutputFrame);
                    outputPos += frameSamples;
                    continue;
                }
            }

            // Check if we should INSERT a frame (output 3-point interpolated without consuming)
            if (_insertEveryNFrames > 0 && _framesSinceLastCorrection >= _insertEveryNFrames)
            {
                _framesSinceLastCorrection = 0;

                var outputSpan = buffer.Slice(outputPos, frameSamples);

                // Peek at the next frames without consuming them, and let the shared splice kernel
                // degrade from a 3-point blend to a 2-point one and then to a hold as they run out.
                if (_count - samplesConsumed >= frameSamples * 2)
                {
                    // Peek at next frame (position 0 in buffer)
                    Span<float> nextFrame = stackalloc float[frameSamples];
                    PeekSamplesFromBuffer(nextFrame, frameSamples);

                    // Peek at frame after next (position 1 in buffer) - need offset peek
                    Span<float> frameAfterNext = stackalloc float[frameSamples];
                    PeekSamplesFromBufferAtOffset(frameAfterNext, frameSamples, frameSamples);

                    SpliceBlend.Blend(_lastOutputFrame, nextFrame, frameAfterNext, outputSpan);
                }
                else if (_count - samplesConsumed >= frameSamples)
                {
                    Span<float> nextFrame = stackalloc float[frameSamples];
                    PeekSamplesFromBuffer(nextFrame, frameSamples);

                    SpliceBlend.Blend(_lastOutputFrame, nextFrame, default, outputSpan);
                }
                else
                {
                    SpliceBlend.Blend(_lastOutputFrame, default, default, outputSpan);
                }

                // Save the spliced frame for continuity
                outputSpan.CopyTo(_lastOutputFrame);

                outputPos += frameSamples;
                _samplesInsertedForSync += frameSamples;

                // Don't increment samplesConsumed - we didn't consume from buffer
                continue;
            }

            // Normal frame: read from buffer and output
            var frameSpan = buffer.Slice(outputPos, frameSamples);
            ReadSamplesFromBuffer(frameSpan);
            samplesConsumed += frameSamples;

            // Save as last output frame for future corrections
            frameSpan.CopyTo(_lastOutputFrame);
            outputPos += frameSamples;
        }

        return (samplesConsumed, outputPos);
    }

    /// <summary>
    /// Logs underrun events with rate limiting to prevent log spam.
    /// Must be called under lock.
    /// </summary>
    /// <remarks>
    /// During severe underruns, this method can be called many times per second
    /// (once per audio callback, typically every ~10ms). Rate limiting ensures
    /// we log at most once per second while still capturing the total count.
    /// </remarks>
    private void LogUnderrunIfNeeded(long currentLocalTime)
    {
        // Check if enough time has passed since last log
        if (currentLocalTime - _lastUnderrunLogTime < UnderrunLogIntervalMicroseconds)
        {
            return;
        }

        // Log the accumulated underruns
        _logger.LogWarning(
            "[Buffer] Underrun: {Count} events in last {IntervalMs}ms (total: {TotalCount}). " +
            "Buffer empty, outputting silence. Check network/decoding performance.",
            _underrunsSinceLastLog,
            (currentLocalTime - _lastUnderrunLogTime) / 1000,
            _underrunCount);

        // Reset rate limit state
        _lastUnderrunLogTime = currentLocalTime;
        _underrunsSinceLastLog = 0;
    }

    /// <summary>
    /// Represents a segment of samples with its target playback time.
    /// </summary>
    /// <param name="ServerTimestamp">Raw server timestamp (microseconds) of this segment's
    /// FIRST sample, unchanged for the segment's whole life — partial consumption is tracked
    /// separately in <see cref="_headConsumedSamples"/>, so this stays a fixed reference point
    /// for the content cursor rather than something that has to be kept in step.
    /// Stored unconverted: clock sync may not have converged when the segment was enqueued
    /// (e.g. the initial burst on a mid-track join arrives before the first time-sync round
    /// completes), so local playback time must be derived at read time via
    /// <see cref="IClockSynchronizer.ServerToClientTime"/> using the current sync state.</param>
    /// <param name="SampleCount">Number of interleaved samples in this segment.</param>
    private readonly record struct TimestampedSegment(long ServerTimestamp, int SampleCount);
}
