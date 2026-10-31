using System.Security.Cryptography;
using System.Text;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

/// <summary>
/// Every ordered pair of a spread of titles, scored both ways. The pin test compares a digest of all of them, so no
/// pair can change by a single bit unseen; the pinned pairs next to it say which pair moved.
/// </summary>
internal static class TitleSimilarityGrid
{
    public static readonly string?[] Titles =
    [
        "The Matrix", "Matrix, The", "Alien", "Aliens", "Alien 1979", "Alien (1979)", "Alien 190a",
        "Madden NFL 2005", "Madden NFL 2004", "Madden NFL", "Metroid Prime", "Metroid Prime 2", "Metroid Prime 2: Echoes",
        "Kitchen Confidential", "Kitchen Confidential: Adventures in the Culinary Underbelly",
        "Kitchen Orchard", "The Orchard Kitchen", "Orchard Kitchen: Recipes for a Small Plot",
        "DUNE FRANK HERBERT", "Dune", "Dune Messiah", "Dune (Dune Chronicles, Book 1)", "Frank Herbert", "Herbert, Frank",
        "Mara Odell", "Odell, Mara", "Odell Mara", "Final Fantasy VII", "Final Fantasy 7", "Final Fantasy VIII",
        "Rocky IV", "Rocky 4", "Rocky III", "Mega Man X", "Mega Man 10", "Ratchet & Clank", "Ratchet and Clank", "Salt & Pepper",
        "Pokémon Red Version", "Pokemon Red Version", "Crème Brûlée à la Française", "Creme Brulee a la Francaise",
        "Sleepaway Camp III: Teenage Wasteland", "SLEEP AWAY CAMP 3 TEENAGE WASTELAND",
        "Star Wars - Episode IV - A New Hope (Widescreen Edition)", "Star Wars: A New Hope",
        "Zelda: Ocarina of Time", "The Legend of Zelda: Ocarina of Time", "Zelda: Majora's Mask",
        "Super Mario 64", "Super Mario 64 DS", "Player's Choice", "Players Choice", "Greatest Hits",
        "2000s", "1984", "Up", "!!!", "Camp Camp Camp", "Camp III", "Harbor Light Keepers", "Harbor Light 2",
        "Dr. Strangelove or: How I Learned to Stop Worrying and Love the Bomb", "Wonder Woman, A", "A Wonder Woman",
        "Harbor Cup 2005", "Harbor Cup 2004 2005", "", null,
    ];

    public static string Digest()
    {
        var lines = new StringBuilder();
        foreach (var a in Titles)
        {
            foreach (var b in Titles)
            {
                lines.Append(Bits(TitleNormalizer.Similarity(a, b))).Append(' ').Append(Bits(TitleNormalizer.TitleSimilarity(a, b))).Append('\n');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString())));
    }

    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
}
