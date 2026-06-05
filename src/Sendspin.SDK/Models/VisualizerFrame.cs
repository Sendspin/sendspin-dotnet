namespace Sendspin.SDK.Models;

/// <summary>
/// One visualizer feature frame parsed from a binary visualizer message (the <c>visualizer@v1</c>
/// role). Each binary message carries exactly one feature type, so any given frame populates only
/// the fields for that type; the rest are null.
/// </summary>
public sealed class VisualizerFrame
{
    /// <summary>Server clock time in microseconds this frame corresponds to.</summary>
    public long Timestamp { get; init; }

    /// <summary>Loudness (binary type 16), 0-65535 mapping a [0,1] dB-normalized scale.</summary>
    public int? Loudness { get; init; }

    /// <summary>Dominant frequency in Hz (binary type 18, paired with <see cref="FPeakAmplitude"/>).</summary>
    public int? FPeakFrequency { get; init; }

    /// <summary>Amplitude of the dominant frequency (binary type 18), 0-65535 dB-normalized.</summary>
    public int? FPeakAmplitude { get; init; }

    /// <summary>
    /// Display-binned spectrum magnitudes (binary type 19), one value (0-65535) per negotiated
    /// display bin.
    /// </summary>
    public IReadOnlyList<int>? Spectrum { get; init; }

    /// <summary>Energy-onset strength (binary type 20), 0-255.</summary>
    public int? PeakStrength { get; init; }

    /// <summary>
    /// Perceived pitch as a MIDI note in Q8.8 fixed point (binary type 21); divide by 256 for the
    /// MIDI note number. Paired with <see cref="PitchConfidence"/>. See <see cref="PitchMidi"/>.
    /// </summary>
    public int? PitchMidiQ88 { get; init; }

    /// <summary>Pitch confidence (binary type 21), 0-255.</summary>
    public int? PitchConfidence { get; init; }

    /// <summary>True for a downbeat, false for a regular beat (binary type 17). Null for non-beat frames.</summary>
    public bool? IsDownbeat { get; init; }

    /// <summary>The pitch as a fractional MIDI note number, or null when this is not a pitch frame.</summary>
    public double? PitchMidi => PitchMidiQ88 is { } q ? q / 256.0 : null;
}
