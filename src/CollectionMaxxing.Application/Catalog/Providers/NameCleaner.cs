using System.Text;
using System.Text.RegularExpressions;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Catalog.Providers;

/// <summary>What the <see cref="NameCleaner"/> pulled out of a product name.</summary>
public sealed record CleanedName(string Title, string? Platform, ReleaseFormat? Format, int? Year);

/// <summary>
/// Turns a UPC database's product name ("Super Mario 64 - Nintendo 64 Cartridge Only [Brand New]") into a search query:
/// the title, plus the platform, format and year if they were there (docs/02 section 3). Deterministic, no AI.
/// </summary>
/// <remarks>
/// It is deliberately conservative, because a title wrongly trimmed is worse than a title left a little long (the scorer
/// forgives extra words, not missing ones):
/// <list type="bullet">
/// <item>Bracketed text is removed only when every part of it is something we know (a format, platform, year or noise
/// such as "Widescreen"); "(Director's Cut)" stays.</item>
/// <item>Format and platform words are taken only from the end of the name, so "Wii Sports" and "PlayStation All-Stars
/// Battle Royale" are left alone.</item>
/// <item>A platform word that is also an ordinary word (Wii, Switch, DS, Genesis, Saturn, GB) is taken only when
/// something marks it as a platform: a separator or "for" before it, or brackets. "Mario Kart Wii" keeps its Wii.</item>
/// <item>A year is read only from brackets; "Madden NFL 2005" keeps its year.</item>
/// <item>A name made only of such words keeps them as the title, so "DVD" is not cleaned to nothing.</item>
/// </list>
/// </remarks>
public static partial class NameCleaner
{
    // Phrases removed wherever they appear. Each is long or odd enough not to turn up in a real title.
    private static readonly (string Phrase, ReleaseFormat? Format)[] NoisePhrases =
    [
        ("brand new", null), ("factory sealed", null), ("new sealed", null), ("new in box", null),
        ("complete in box", null), ("like new", null), ("pre-owned", null), ("free shipping", null),
        ("cartridge only", ReleaseFormat.GameCartridge), ("loose cartridge", ReleaseFormat.GameCartridge),
        ("game only", null), ("disc only", null), ("case only", null),
        ("widescreen edition", null), ("full screen edition", null), ("widescreen", null), ("fullscreen", null),
        ("full screen", null),
    ];

    // Extra things that are noise inside brackets only ("(NTSC)", "(Rated R)").
    private static readonly HashSet<string> BracketNoise = new(StringComparer.Ordinal)
    {
        "ntsc", "pal", "ntsc j", "region 1", "region 2", "region free", "rated r", "rated pg", "rated pg 13", "unrated",
        "widescreen", "fullscreen", "full screen", "widescreen edition", "full screen edition", "color", "closed captioned",
    };

    // Trailing words that say nothing about the title.
    private static readonly HashSet<string> TrailingNoise = new(StringComparer.Ordinal) { "video game" };

    private static readonly Dictionary<string, ReleaseFormat> FormatPhrases = new(StringComparer.Ordinal)
    {
        ["dvd"] = ReleaseFormat.Dvd,
        ["dvd video"] = ReleaseFormat.Dvd,
        ["blu ray"] = ReleaseFormat.Bluray,
        ["bluray"] = ReleaseFormat.Bluray,
        ["blu ray disc"] = ReleaseFormat.Bluray,
        ["4k ultra hd blu ray"] = ReleaseFormat.UhdBluray,
        ["4k ultra hd blu ray disc"] = ReleaseFormat.UhdBluray,
        ["4k blu ray"] = ReleaseFormat.UhdBluray,
        ["4k ultra hd"] = ReleaseFormat.UhdBluray,
        ["4k uhd"] = ReleaseFormat.UhdBluray,
        ["ultra hd"] = ReleaseFormat.UhdBluray,
        ["uhd"] = ReleaseFormat.UhdBluray,
        ["vhs"] = ReleaseFormat.Vhs,
        ["vhs tape"] = ReleaseFormat.Vhs,
        ["betamax"] = ReleaseFormat.Betamax,
        ["laserdisc"] = ReleaseFormat.Laserdisc,
        ["laser disc"] = ReleaseFormat.Laserdisc,
        ["hardcover"] = ReleaseFormat.Hardcover,
        ["hard cover"] = ReleaseFormat.Hardcover,
        ["paperback"] = ReleaseFormat.Paperback,
        ["mass market paperback"] = ReleaseFormat.MassMarket,
        ["mass market"] = ReleaseFormat.MassMarket,
        ["game cartridge"] = ReleaseFormat.GameCartridge,
        ["cartridge"] = ReleaseFormat.GameCartridge,
        ["game disc"] = ReleaseFormat.GameDisc,
        ["game card"] = ReleaseFormat.GameCard,
    };

    // Platform names that are also ordinary words or abbreviations: taken only when marked as a platform.
    private static readonly HashSet<string> RiskyPlatformPhrases = new(StringComparer.Ordinal)
    {
        "wii", "switch", "ds", "genesis", "saturn", "gb", "vita", "xbox", "ngc", "gc",
    };

    private const int MaxPhraseTokens = 5;

    private static readonly Regex NoiseRegex = BuildNoiseRegex();

    [GeneratedRegex(@"[\(\[]([^\(\)\[\]]*)[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketGroup();

    [GeneratedRegex(@"^(?:19|20)\d\d$", RegexOptions.CultureInvariant)]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>Cleans one product name. Never throws; a null or empty name gives an empty title.</summary>
    public static CleanedName Clean(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return new CleanedName(string.Empty, null, null, null);
        }

        var found = new Extraction();
        var text = Whitespace().Replace(productName.Trim(), " ");

        text = BracketGroup().Replace(text, match => TryTakeGroup(match.Groups[1].Value, found) ? " " : match.Value);

        text = NoiseRegex.Replace(text, match =>
        {
            foreach (var (phrase, format) in NoisePhrases)
            {
                if (format is not null && PlatformNames.Normalize(match.Value) == PlatformNames.Normalize(phrase))
                {
                    found.SetFormat(format.Value);
                }
            }

            return " ";
        });

        text = PopTrailingWords(Whitespace().Replace(text, " ").Trim(), found);
        text = Trim(Whitespace().Replace(text, " "));

        return new CleanedName(text, found.Platform, found.Format, found.Year);
    }

    // "(Blu-ray Disc, 2012)" or "[DVD]": taken (removed) only when every comma-separated part is known.
    private static bool TryTakeGroup(string content, Extraction found)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        var staged = new Extraction();
        foreach (var raw in content.Split(',', ';', '/'))
        {
            var part = PlatformNames.Normalize(raw);
            if (part.Length == 0)
            {
                continue;
            }

            if (YearPattern().IsMatch(raw.Trim()))
            {
                staged.SetYear(int.Parse(raw.Trim(), System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (FormatPhrases.TryGetValue(part, out var format))
            {
                staged.SetFormat(format);
            }
            else if (PlatformNames.TryGetCanonical(part, out var platform))
            {
                staged.SetPlatform(platform);
            }
            else if (BracketNoise.Contains(part) || NoisePhrases.Any(n => PlatformNames.Normalize(n.Phrase) == part))
            {
                continue;
            }
            else
            {
                return false;
            }
        }

        found.Merge(staged);
        return true;
    }

    // Pops format, platform and "video game" words off the end, longest phrase first, never leaving the title empty.
    private static string PopTrailingWords(string text, Extraction found)
    {
        while (true)
        {
            text = TrimTrailingSeparators(text);
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var popped = false;

            for (var n = Math.Min(MaxPhraseTokens, tokens.Length - 1); n >= 1 && !popped; n--)
            {
                var phrase = PlatformNames.Normalize(string.Join(' ', tokens[^n..]));
                var before = string.Join(' ', tokens[..^n]);

                if (FormatPhrases.TryGetValue(phrase, out var format))
                {
                    found.SetFormat(format);
                    popped = true;
                }
                else if (PlatformNames.TryGetCanonical(phrase, out var platform)
                    && (!RiskyPlatformPhrases.Contains(phrase) || IsMarkedAsPlatform(before)))
                {
                    found.SetPlatform(platform);
                    before = DropTrailingFor(before);
                    popped = true;
                }
                else if (TrailingNoise.Contains(phrase))
                {
                    popped = true;
                }

                if (popped)
                {
                    text = before;
                }
            }

            if (!popped)
            {
                return text;
            }
        }
    }

    // "Resident Evil 4 - " or "Wii Fit Plus for " (the text before the candidate platform word).
    private static bool IsMarkedAsPlatform(string before)
    {
        var trimmed = before.TrimEnd();
        return trimmed.Length > 0
            && (IsSeparator(trimmed[^1]) || trimmed.EndsWith(" for", StringComparison.OrdinalIgnoreCase));
    }

    private static string DropTrailingFor(string before)
    {
        var trimmed = before.TrimEnd();
        return trimmed.EndsWith(" for", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : before;
    }

    private static bool IsSeparator(char c) => c is '-' or '–' or '—' or ':' or ',' or ';' or '|' or '/';

    private static string TrimTrailingSeparators(string text)
    {
        var end = text.Length;
        while (end > 0 && (char.IsWhiteSpace(text[end - 1]) || IsSeparator(text[end - 1])))
        {
            end--;
        }

        return text[..end];
    }

    private static string Trim(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && (char.IsWhiteSpace(text[start]) || IsSeparator(text[start])))
        {
            start++;
        }

        while (end > start && (char.IsWhiteSpace(text[end - 1]) || IsSeparator(text[end - 1])))
        {
            end--;
        }

        return text[start..end];
    }

    private static Regex BuildNoiseRegex()
    {
        var alternatives = NoisePhrases
            .OrderByDescending(n => n.Phrase.Length)
            .Select(n => string.Join(@"[\s\-]+", n.Phrase.Split(' ', '-').Select(Regex.Escape)));
        var pattern = $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join('|', alternatives)})(?![\p{{L}}\p{{N}}])";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private sealed class Extraction
    {
        public string? Platform { get; private set; }

        public ReleaseFormat? Format { get; private set; }

        public int? Year { get; private set; }

        // The first one found wins, except that a 4K disc beats the plain Blu-ray it is also called.
        public void SetFormat(ReleaseFormat format)
        {
            if (Format is null || (Format == ReleaseFormat.Bluray && format == ReleaseFormat.UhdBluray))
            {
                Format = format;
            }
        }

        public void SetPlatform(string platform) => Platform ??= platform;

        public void SetYear(int year) => Year ??= year;

        public void Merge(Extraction other)
        {
            if (other.Format is { } format)
            {
                SetFormat(format);
            }

            if (other.Platform is { } platform)
            {
                SetPlatform(platform);
            }

            if (other.Year is { } year)
            {
                SetYear(year);
            }
        }
    }
}
