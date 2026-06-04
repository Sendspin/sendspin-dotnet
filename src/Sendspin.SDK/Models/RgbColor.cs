using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sendspin.SDK.Models;

/// <summary>
/// An RGB color (0-255 per channel) carried by the <c>color</c> role. Serializes to and from the
/// Sendspin wire form, a three-element JSON array <c>[R, G, B]</c>.
/// </summary>
[JsonConverter(typeof(RgbColorJsonConverter))]
public readonly struct RgbColor : IEquatable<RgbColor>
{
    /// <summary>Red channel (0-255).</summary>
    public byte R { get; }

    /// <summary>Green channel (0-255).</summary>
    public byte G { get; }

    /// <summary>Blue channel (0-255).</summary>
    public byte B { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RgbColor"/> struct.
    /// </summary>
    public RgbColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    /// <summary>Deconstructs the color into its channels: <c>var (r, g, b) = color;</c>.</summary>
    public void Deconstruct(out byte r, out byte g, out byte b) => (r, g, b) = (R, G, B);

    /// <inheritdoc/>
    public bool Equals(RgbColor other) => R == other.R && G == other.G && B == other.B;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RgbColor other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(R, G, B);

    public static bool operator ==(RgbColor left, RgbColor right) => left.Equals(right);

    public static bool operator !=(RgbColor left, RgbColor right) => !left.Equals(right);

    /// <summary>Returns the color as a hex string (e.g. <c>#FF8000</c>).</summary>
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Serializes <see cref="RgbColor"/> to and from the wire form <c>[R, G, B]</c> (a JSON array of
/// three integers 0-255). Values outside 0-255 are clamped.
/// </summary>
public sealed class RgbColorJsonConverter : JsonConverter<RgbColor>
{
    /// <inheritdoc/>
    public override RgbColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray && TryReadRgb(ref reader, out var color))
        {
            return color;
        }

        throw new JsonException($"Expected a 3-element [R,G,B] array for {nameof(RgbColor)}.");
    }

    /// <summary>
    /// Reads an <c>[R, G, B]</c> array (clamping channels to 0-255). Returns false on any malformed
    /// shape (not an array, non-numeric channel, or not exactly three elements) without throwing.
    /// When called on a <see cref="JsonTokenType.StartArray"/>, the array is fully consumed either
    /// way, leaving the reader at the matching <see cref="JsonTokenType.EndArray"/>.
    /// </summary>
    internal static bool TryReadRgb(ref Utf8JsonReader reader, out RgbColor color)
    {
        color = default;
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return false;
        }

        Span<byte> channels = stackalloc byte[3];
        var count = 0;
        var valid = true;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value))
            {
                if (count < 3)
                {
                    channels[count] = (byte)Math.Clamp(value, 0, 255);
                }
            }
            else
            {
                valid = false;
            }

            count++;
        }

        if (!valid || count != 3)
        {
            return false;
        }

        color = new RgbColor(channels[0], channels[1], channels[2]);
        return true;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, RgbColor value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.R);
        writer.WriteNumberValue(value.G);
        writer.WriteNumberValue(value.B);
        writer.WriteEndArray();
    }
}
