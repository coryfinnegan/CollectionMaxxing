using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CollectionMaxxing.Api.Scanning;

/// <summary>
/// Reads a read's box without ever failing the request. A box is a hint, so one that is not an object, or has a member that is not a
/// number, must not turn a scan or an eval run into a 400: it is read as far as it can be (a member that is not a number is left
/// out), and the server then treats a box with a member missing as no box. Only the identify and eval endpoints add it, to their own
/// copy of the app's JSON options, so the OpenAPI document (which describes the plain <see cref="ItemBox"/>) does not change.
/// </summary>
internal sealed class LenientItemBoxConverter : JsonConverter<ItemBox>
{
    public override ItemBox? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip(); // a string, a number, a list: not a box
            return null;
        }

        double? x = null, y = null, width = null, height = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            double? value = reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var number) ? number : null;
            reader.Skip(); // an object or list as a member's value is stepped over; a plain value is already read

            if (string.Equals(name, "x", StringComparison.OrdinalIgnoreCase))
            {
                x = value;
            }
            else if (string.Equals(name, "y", StringComparison.OrdinalIgnoreCase))
            {
                y = value;
            }
            else if (string.Equals(name, "width", StringComparison.OrdinalIgnoreCase))
            {
                width = value;
            }
            else if (string.Equals(name, "height", StringComparison.OrdinalIgnoreCase))
            {
                height = value;
            }
        }

        return new ItemBox(x, y, width, height);
    }

    public override void Write(Utf8JsonWriter writer, ItemBox value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        Member(writer, "x", value.X);
        Member(writer, "y", value.Y);
        Member(writer, "width", value.Width);
        Member(writer, "height", value.Height);
        writer.WriteEndObject();
    }

    private static void Member(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}

/// <summary>The app's JSON options with <see cref="LenientItemBoxConverter"/> added, made once for each set of app options.</summary>
internal static class BoxTolerantJson
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Made = new();

    public static JsonSerializerOptions Of(JsonSerializerOptions app) => Made.GetValue(app, Create);

    private static JsonSerializerOptions Create(JsonSerializerOptions app)
    {
        var tolerant = new JsonSerializerOptions(app);
        tolerant.Converters.Insert(0, new LenientItemBoxConverter());
        return tolerant;
    }
}
