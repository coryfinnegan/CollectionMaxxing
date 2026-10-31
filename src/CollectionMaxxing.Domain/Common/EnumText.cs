using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CollectionMaxxing.Domain.Common;

/// <summary>
/// The one spelling of an enum value: lower snake_case (<c>VeryGood</c> is <c>very_good</c>), unless the member says
/// otherwise with <see cref="JsonStringEnumMemberNameAttribute"/>. The JSON API and the database both use it, so they agree.
/// </summary>
/// <remarks>Entity Framework converts every enum column through here, so each enum's spellings are worked out once and then looked up.</remarks>
public static class EnumText
{
    public static string ToText<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        Spellings<TEnum>.Texts.TryGetValue(value, out var text) ? text : Spellings<TEnum>.Spell(value);

    public static TEnum Parse<TEnum>(string text)
        where TEnum : struct, Enum =>
        TryParse<TEnum>(text, out var value)
            ? value
            : throw new ArgumentException($"'{text}' is not a {typeof(TEnum).Name}.", nameof(text));

    public static bool TryParse<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        if (text is not null)
        {
            return Spellings<TEnum>.Values.TryGetValue(text, out value);
        }

        value = default;
        return false;
    }

    /// <summary>The member spelled <paramref name="text"/>, or null when there is no text or no such member.</summary>
    public static TEnum? ParseOrNull<TEnum>(string? text)
        where TEnum : struct, Enum =>
        TryParse<TEnum>(text, out var value) ? value : null;

    /// <summary>Every spelling of <typeparamref name="TEnum"/> in declaration order, e.g. for a database check constraint.</summary>
    public static IReadOnlyList<string> All<TEnum>()
        where TEnum : struct, Enum =>
        Spellings<TEnum>.All;

    private static class Spellings<TEnum>
        where TEnum : struct, Enum
    {
        public static readonly ReadOnlyCollection<string> All;
        public static readonly FrozenDictionary<TEnum, string> Texts;
        public static readonly FrozenDictionary<string, TEnum> Values;

        static Spellings()
        {
            var members = Enum.GetValues<TEnum>();
            var all = new string[members.Length];
            var texts = new Dictionary<TEnum, string>(members.Length);
            var values = new Dictionary<string, TEnum>(members.Length, StringComparer.Ordinal);
            for (var i = 0; i < members.Length; i++)
            {
                all[i] = Spell(members[i]);
                texts[members[i]] = all[i];
                values.TryAdd(all[i], members[i]);
            }

            All = Array.AsReadOnly(all);
            Texts = texts.ToFrozenDictionary();
            Values = values.ToFrozenDictionary(StringComparer.Ordinal);
        }

        /// <summary>Works out a spelling from the member itself. Also spells a number that is no declared member, which has nothing cached.</summary>
        public static string Spell(TEnum value)
        {
            var name = value.ToString();
            return typeof(TEnum).GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
                ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
        }
    }
}
