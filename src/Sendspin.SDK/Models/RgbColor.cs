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
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected an [R,G,B] array for {nameof(RgbColor)}, got {reader.TokenType}.");
        }

        Span<byte> channels = stackalloc byte[3];
        var count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException($"Expected a numeric color channel for {nameof(RgbColor)}.");
            }

            var value = Math.Clamp(reader.GetInt32(), 0, 255);
            if (count < 3)
            {
                channels[count] = (byte)value;
            }

            count++;
        }

        if (count != 3)
        {
            throw new JsonException($"Expected exactly 3 channels for {nameof(RgbColor)}, got {count}.");
        }

        return new RgbColor(channels[0], channels[1], channels[2]);
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
