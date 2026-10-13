using System.Text;

namespace CollectionMaxxing.Domain.Catalog.Matching;

/// <summary>
/// One canonical name per games platform, so "N64", "Nintendo 64" and "nintendo-64" agree. The scorer uses it to decide
/// whether two platform names are the same console; the NameCleaner uses it to spot platform words in product names.
/// </summary>
public static class PlatformNames
{
    private static readonly (string Canonical, string[] Aliases)[] Table =
    [
        ("NES", ["nes", "nintendo entertainment system", "nintendo nes", "famicom"]),
        ("SNES", ["snes", "super nes", "super nintendo", "super nintendo entertainment system", "super famicom", "nintendo snes"]),
        ("N64", ["n64", "nintendo 64", "nintendo64"]),
        ("GameCube", ["gamecube", "game cube", "nintendo gamecube", "nintendo game cube", "ngc"]),
        ("Wii", ["wii", "nintendo wii"]),
        ("Wii U", ["wii u", "nintendo wii u"]),
        ("Switch", ["switch", "nintendo switch"]),
        ("Game Boy", ["game boy", "gameboy", "nintendo game boy", "nintendo gameboy", "gb"]),
        ("Game Boy Color", ["game boy color", "gameboy color", "nintendo game boy color", "gbc"]),
        ("Game Boy Advance", ["game boy advance", "gameboy advance", "nintendo game boy advance", "gba"]),
        ("DS", ["ds", "nds", "nintendo ds"]),
        ("3DS", ["3ds", "nintendo 3ds"]),
        ("PS1", ["ps1", "psx", "ps one", "playstation", "playstation 1", "sony playstation", "sony playstation 1"]),
        ("PS2", ["ps2", "playstation 2", "sony playstation 2"]),
        ("PS3", ["ps3", "playstation 3", "sony playstation 3"]),
        ("PS4", ["ps4", "playstation 4", "sony playstation 4"]),
        ("PS5", ["ps5", "playstation 5", "sony playstation 5"]),
        ("PSP", ["psp", "playstation portable", "sony psp"]),
        ("Vita", ["vita", "ps vita", "psvita", "playstation vita", "sony playstation vita"]),
        ("Xbox", ["xbox", "microsoft xbox", "original xbox"]),
        ("Xbox 360", ["xbox 360", "xbox360", "x360", "microsoft xbox 360"]),
        ("Xbox One", ["xbox one", "xb1", "microsoft xbox one"]),
        ("Xbox Series X", ["xbox series x", "xbox series s", "xbox series x s", "microsoft xbox series x"]),
        ("Genesis", ["genesis", "sega genesis", "mega drive", "megadrive", "sega mega drive"]),
        ("Master System", ["master system", "sega master system"]),
        ("Saturn", ["saturn", "sega saturn"]),
        ("Dreamcast", ["dreamcast", "sega dreamcast"]),
        ("Game Gear", ["game gear", "sega game gear"]),
    ];

    private static readonly Dictionary<string, string> AliasToCanonical = BuildAliasMap();

    /// <summary>
    /// The canonical name of a platform: "Nintendo 64" gives "N64". A platform this table does not know comes back tidied
    /// (lower case, single spaces, no punctuation), so two spellings of the same unknown platform still agree.
    /// Null, empty and punctuation-only input give null.
    /// </summary>
    public static string? Canonicalize(string? name)
    {
        var key = Normalize(name);
        if (key.Length == 0)
        {
            return null;
        }

        return AliasToCanonical.TryGetValue(key, out var canonical) ? canonical : key;
    }

    /// <summary>Looks up a phrase that is a known name of a platform. The phrase is matched ignoring case and punctuation.</summary>
    public static bool TryGetCanonical(string? phrase, out string canonical)
    {
        if (AliasToCanonical.TryGetValue(Normalize(phrase), out var found))
        {
            canonical = found;
            return true;
        }

        canonical = string.Empty;
        return false;
    }

    /// <summary>Lower case, letters and digits only, single spaces between words.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    private static Dictionary<string, string> BuildAliasMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (canonical, aliases) in Table)
        {
            foreach (var alias in aliases)
            {
                map[alias] = canonical;
            }

            map[Normalize(canonical)] = canonical;
        }

        return map;
    }
}
