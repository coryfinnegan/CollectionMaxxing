using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CollectionMaxxing.Domain.Common;

/// <summary>
/// The one spelling of an enum value: lower snake_case (<c>VeryGood</c> is <c>very_good</c>), unless the member says
/// otherwise with <see cref="JsonStringEnumMemberNameAttribute"/>. The JSON API and the database both use it, so they agree.
/// </summary>
public static class EnumText
{
    public static string ToText<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        var name = value.ToString();
        var member = typeof(TEnum).GetField(name, BindingFlags.Public | BindingFlags.Static);
        return member?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
            ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
    }

    public static TEnum Parse<TEnum>(string text)
        where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (ToText(value) == text)
            {
                return value;
            }
        }

        throw new ArgumentException($"'{text}' is not a {typeof(TEnum).Name}.", nameof(text));
    }

    /// <summary>Every spelling of <typeparamref name="TEnum"/>, e.g. for a database check constraint.</summary>
    public static IReadOnlyList<string> All<TEnum>()
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(ToText).ToList();
}
