// <copyright file="DiagnosticAudioRingBuffer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Diagnostics;

/// <summary>
/// Lock-free single-producer single-consumer ring buffer for diagnostic audio
/// capture. The audio thread writes (must never block) and a background save
/// thread reads. When full, old samples are overwritten.
/// </summary>
public sealed class DiagnosticAudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacity;
    private readonly int _mask;

    // Only modified by the producer (audio thread).
    private long _writeIndex;

    private readonly int _sampleRate;
    private readonly int _channels;

    /// <summary>
    /// Gets the sample rate of the audio in this buffer.
    /// </summary>
    public int SampleRate => _sampleRate;

    /// <summary>
    /// Gets the number of channels in the audio.
    /// </summary>
    public int Channels => _channels;

    /// <summary>
    /// Gets the buffer capacity in samples.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets the total number of samples written since creation.
    /// This is a cumulative count that can exceed capacity (wraps around).
    /// </summary>
    public long TotalSamplesWritten => Volatile.Read(ref _writeIndex);

    /// <summary>
    /// Gets the duration of audio currently in the buffer in seconds.
    /// </summary>
    public double BufferedSeconds
    {
        get
        {
            var samplesWritten = Volatile.Read(ref _writeIndex);
            var samplesAvailable = Math.Min(samplesWritten, _capacity);
            return (double)samplesAvailable / _sampleRate / _channels;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticAudioRingBuffer"/> class.
    /// </summary>
    /// <param name="sampleRate">The audio sample rate (e.g., 48000).</param>
    /// <param name="channels">The number of audio channels (e.g., 2 for stereo).</param>
    /// <param name="durationSeconds">The buffer duration in seconds. Will be rounded up to power of 2.</param>
    public DiagnosticAudioRingBuffer(int sampleRate, int channels, int durationSeconds = 45)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(channels, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(durationSeconds, 0);

        _sampleRate = sampleRate;
        _channels = channels;

        // Calculate required capacity and round up to next power of 2.
        // Use checked to prevent silent overflow producing a tiny buffer.
        var requiredSamples = checked(sampleRate * channels * durationSeconds);
        _capacity = RoundUpToPowerOfTwo(requiredSamples);
        _mask = _capacity - 1;
        _buffer = new float[_capacity];
    }

    /// <summary>
    /// Writes samples to the buffer. Called from the audio thread; never blocks.
    /// When full, the oldest samples are overwritten.
    /// </summary>
    /// <param name="samples">The samples to write.</param>
    public void Write(ReadOnlySpan<float> samples)
    {
        var writeIdx = Volatile.Read(ref _writeIndex);

        // Hot path: write index masked to capacity for wrap-around (capacity is a power of 2).
        foreach (var sample in samples)
        {
            _buffer[writeIdx & _mask] = sample;
            writeIdx++;
        }

        Volatile.Write(ref _writeIndex, writeIdx);
    }

    /// <summary>
    /// Captures a snapshot of the current buffer contents. Allocates a new array;
    /// must be called from a background thread (not the audio thread). Returns
    /// (samples, startIndex) where startIndex is the cumulative sample index of
    /// the first returned sample, useful for correlating with
    /// <see cref="SyncMetricSnapshot.SamplePosition"/>.
    /// </summary>
    public (float[] Samples, long StartIndex) CaptureSnapshot()
    {
        var writeIdx = Volatile.Read(ref _writeIndex);
        var samplesAvailable = (int)Math.Min(writeIdx, _capacity);
        var startIdx = writeIdx - samplesAvailable;

        var result = new float[samplesAvailable];
        for (var i = 0; i < samplesAvailable; i++)
        {
            result[i] = _buffer[(startIdx + i) & _mask];
        }

        return (result, startIdx);
    }

    /// <summary>
    /// Resets the buffer to an empty state. Must only be called when the
    /// audio thread is not writing.
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _writeIndex, 0);
        Array.Clear(_buffer);
    }

    /// <summary>Rounds a value up to the next power of 2.</summary>
    private static int RoundUpToPowerOfTwo(int value)
    {
        if (value <= 0)
        {
            return 1;
        }

        // Standard bit-smearing idiom: decrement, OR-shift to fill all lower bits
        // with the highest set bit, then increment to land on the next power of 2.
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }
}
