using System.Text.Json;
using CollectionMaxxing.Application.ShelfReading;

namespace CollectionMaxxing.Infrastructure.ShelfReading;

/// <summary>
/// Pulls the complete items out of <c>{ "items": [ ... ] }</c>, including from JSON that stops part way (a read that hit the
/// token cap). Only objects that closed are parsed; blank titles are dropped, text is trimmed and an unknown kind is none.
/// </summary>
internal static class ItemScanner
{
    internal sealed record Scanned(List<ShelfReadItem> Items, bool SawItemsArray, bool ArrayClosed);

    private const string Key = "\"items\"";

    public static Scanned Scan(string json)
    {
        var items = new List<ShelfReadItem>();
        var at = json.IndexOf(Key, StringComparison.Ordinal);
        if (at < 0)
        {
            return new Scanned(items, false, false);
        }

        var i = at + Key.Length;
        while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ':'))
        {
            i++;
        }

        if (i >= json.Length || json[i] != '[')
        {
            return new Scanned(items, false, false);
        }

        i++;
        while (i < json.Length)
        {
            var c = json[i];
            if (c == ']')
            {
                return new Scanned(items, true, true);
            }

            if (c != '{')
            {
                i++; // whitespace and commas
                continue;
            }

            var end = ObjectEnd(json, i);
            if (end < 0)
            {
                break; // the last object never closed
            }

            if (Parse(json.AsSpan(i, end - i + 1)) is { } item)
            {
                items.Add(item);
            }

            i = end + 1;
        }

        return new Scanned(items, true, false);
    }

    /// <summary>The index of the brace closing the object that opens at <paramref name="start"/>, or -1.</summary>
    private static int ObjectEnd(string json, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < json.Length; i++)
        {
            var c = json[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static ShelfReadItem? Parse(ReadOnlySpan<char> objectJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(objectJson.ToString());
            var root = doc.RootElement;
            var title = Text(root, "title");
            if (title.Length == 0)
            {
                return null;
            }

            return new ShelfReadItem(title, Text(root, "creator"), KindOf(Text(root, "kind")));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private static ShelfItemKind KindOf(string kind) => kind.ToLowerInvariant() switch
    {
        "book" => ShelfItemKind.Book,
        "film" => ShelfItemKind.Film,
        "tv" => ShelfItemKind.Tv,
        "game" => ShelfItemKind.Game,
        _ => ShelfItemKind.None,
    };
}
