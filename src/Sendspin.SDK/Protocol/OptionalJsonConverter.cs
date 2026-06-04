using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Protocol;

/// <summary>
/// JSON converter factory for <see cref="Optional{T}"/> that preserves the
/// distinction between an absent field and one explicitly set to null
/// (standard C# nullables collapse both into <c>null</c>).
/// </summary>
/// <remarks>
/// NativeAOT: each <c>Optional&lt;T&gt;</c> used in the protocol must be registered
/// in <see cref="CreateConverter"/> with an explicit instantiation, since the AOT
/// compiler cannot discover reflection-constructed generic types.
/// </remarks>
public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];

        // AOT-safe: explicit converter instantiation instead of Activator.CreateInstance.
        // Add a case here when introducing a new Optional<T> property in protocol messages.
        if (valueType == typeof(PlaybackProgress))
        {
            return new OptionalJsonConverter<PlaybackProgress?>();
        }

        if (valueType == typeof(Sendspin.SDK.Models.RgbColor?))
        {
            return new OptionalRgbColorJsonConverter();
        }

        throw new NotSupportedException(
            $"No AOT-safe converter registered for Optional<{valueType.Name}>. " +
            $"Add an explicit case in {nameof(OptionalJsonConverterFactory)}.{nameof(CreateConverter)}().");
    }
}

/// <summary>
/// JSON converter for <see cref="Optional{T}"/>.
/// </summary>
/// <typeparam name="T">The type of the optional value.</typeparam>
internal sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    /// <inheritdoc />
    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // If we're reading, the field IS present in the JSON
        // (System.Text.Json only calls Read when the property exists)
        if (reader.TokenType == JsonTokenType.Null)
        {
            // Field is present with explicit null value
            return Optional<T>.Present(default);
        }

        // Field is present with a value
        // Use JsonTypeInfo to avoid RequiresUnreferencedCode warning (AOT-friendly)
        var typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        var value = JsonSerializer.Deserialize(ref reader, typeInfo);
        return Optional<T>.Present(value);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        if (value.IsAbsent)
        {
            // Don't write anything - the field should be omitted
            // Note: This requires the containing object to use JsonIgnoreCondition
            // or custom serialization to actually omit the property
            return;
        }

        if (value.Value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }
}

/// <summary>
/// Converter for <see cref="Optional{T}"/> of a nullable <see cref="RgbColor"/> on the <c>color</c>
/// object. Unlike the generic converter, a malformed color value degrades to
/// <see cref="Optional{T}.Absent"/> (treated as "no change") instead of throwing — so one bad color
/// from the server cannot abort the whole <c>server/state</c> message and drop co-resident
/// metadata/controller updates. An explicit JSON <c>null</c> is preserved as a "clear" instruction.
/// </summary>
internal sealed class OptionalRgbColorJsonConverter : JsonConverter<Optional<RgbColor?>>
{
    /// <inheritdoc />
    public override Optional<RgbColor?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return Optional<RgbColor?>.Present(null);
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            // TryReadRgb fully consumes the array (to EndArray) whether or not it is well-formed.
            return RgbColorJsonConverter.TryReadRgb(ref reader, out var color)
                ? Optional<RgbColor?>.Present(color)
                : Optional<RgbColor?>.Absent();
        }

        // Some other token (object/number/string): consume it and treat as no change.
        reader.Skip();
        return Optional<RgbColor?>.Absent();
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Optional<RgbColor?> value, JsonSerializerOptions options)
    {
        if (value.IsAbsent)
        {
            return;
        }

        if (value.Value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            JsonSerializer.Serialize(writer, value.Value.Value, options);
        }
    }
}
