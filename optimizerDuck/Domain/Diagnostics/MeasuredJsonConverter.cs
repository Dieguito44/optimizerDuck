using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace optimizerDuck.Domain.Diagnostics;

/// <summary>
///     Writes <see cref="Measured{T}" /> as <c>{ "value": x }</c> or <c>{ "missing": "Reason" }</c>
///     and reads it back, so saved sessions keep "not measured" and its reason.
/// </summary>
public sealed class MeasuredJsonConverter : JsonConverter
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        Converters = { new MeasuredJsonConverter() },
        Formatting = Formatting.Indented,
    };

    public override bool CanConvert(Type objectType) =>
        objectType.IsGenericType && objectType.GetGenericTypeDefinition() == typeof(Measured<>);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        var type = value.GetType();
        var hasValue = (bool)type.GetProperty(nameof(Measured<int>.HasValue))!.GetValue(value)!;
        writer.WriteStartObject();
        if (hasValue)
        {
            writer.WritePropertyName("value");
            serializer.Serialize(
                writer,
                type.GetProperty(nameof(Measured<int>.ValueOrDefault))!.GetValue(value)
            );
        }
        else
        {
            writer.WritePropertyName("missing");
            writer.WriteValue(
                type.GetProperty(nameof(Measured<int>.Reason))!.GetValue(value)!.ToString()
            );
        }
        writer.WriteEndObject();
    }

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer
    )
    {
        var valueType = objectType.GetGenericArguments()[0];
        if (reader.TokenType == JsonToken.Null)
            return Missing(objectType, NotMeasuredReason.NotReported);

        var obj = JObject.Load(reader);
        if (obj.TryGetValue("value", out var token))
        {
            var value = token.ToObject(valueType, serializer);
            return objectType
                .GetMethod(nameof(Measured<int>.Of), BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [value]);
        }

        var reason = Enum.TryParse<NotMeasuredReason>((string?)obj["missing"], out var parsed)
            ? parsed
            : NotMeasuredReason.NotReported;
        return Missing(objectType, reason);
    }

    private static object Missing(Type objectType, NotMeasuredReason reason) =>
        objectType
            .GetMethod(nameof(Measured<int>.Missing), BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [reason])!;
}
