using System.Text.Json;
using System.Text.RegularExpressions;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Infrastructure.Providers;

/// <summary>Forgiving readers for provider JSON: a missing or oddly typed field is "not there", never an exception.</summary>
public static partial class ProviderJson
{
    /// <summary>The longest text taken from a provider field. A provider is untrusted (docs/08, API10): longer text is cut, never passed on whole.</summary>
    public const int MaxTextLength = 2000;

    public static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static string? Str(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString())
            ? Cap(value.GetString()!.Trim())
            : null;

    private static string Cap(string text) => text.Length <= MaxTextLength ? text : text[..MaxTextLength];

    public static long? Num(this JsonElement element, string name) =>
        element.Prop(name) is { } value && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static IEnumerable<string> Strings(this JsonElement element, string name) =>
        element.Items(name).Where(e => e.ValueKind == JsonValueKind.String).Select(e => Cap(e.GetString()!.Trim())).Where(s => s.Length > 0);

    /// <summary>The first four-digit year (1500 to 2099) in text such as "Jun 1999" or "2005-05-17".</summary>
    public static int? Year(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var match = YearPattern().Match(text);
        return match.Success ? int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The valid identifiers among raw barcode strings (ISBN-10s become ISBN-13s), each once.</summary>
    public static IReadOnlyList<Identifier> Identifiers(IEnumerable<string> raw) =>
        raw.Select(r => Identifier.TryParse(r, out var id) ? id : null)
            .OfType<Identifier>()
            .Distinct()
            .ToList();

    [GeneratedRegex(@"(?<!\d)(1[5-9]\d\d|20\d\d)(?!\d)")]
    private static partial Regex YearPattern();
}
