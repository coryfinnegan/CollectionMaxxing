using System.IO.Compression;
using System.Text;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Infrastructure.GameSerials;

/// <summary>Registers the bundled game product-code table (ADR-031).</summary>
public static class GameSerialsServices
{
    /// <summary>The lookup is one table, read on first use and shared. Safe to call more than once.</summary>
    public static IServiceCollection AddGameSerials(this IServiceCollection services)
    {
        services.TryAddSingleton<IGameSerialLookup, GameSerialTable>();
        return services;
    }
}

/// <summary>
/// The product-code table that ships inside this assembly (<c>game-serials.tsv.gz</c>, made by
/// <c>scripts/game-serials/Update-GameSerials.ps1</c> from libretro-database; its README and LICENSE sit beside it), read once, on
/// the first lookup, and kept in memory. Each row is <c>CODE TAB PLATFORM TAB TITLE</c>, sorted; a line starting with <c>#</c> is
/// a comment.
/// </summary>
public sealed class GameSerialTable : IGameSerialLookup
{
    /// <summary>The name of the embedded resource.</summary>
    public const string ResourceName = "CollectionMaxxing.Infrastructure.GameSerials.game-serials.tsv.gz";

    private static readonly GameSerialMatch[] None = [];

    private readonly Lazy<Dictionary<string, GameSerialMatch[]>> _byCode =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null)
    {
        if (ProductCode.Normalize(code) is not { } normal)
        {
            return None;
        }

        // A platform the matcher knows is a claim; a stray word ("boxed") is not, so it can't block a code that is right.
        var wanted = PlatformNames.TryGetCanonical(platform, out var canonical) ? canonical : null;
        var table = _byCode.Value;

        var found = Matches(table, normal, wanted);
        if (found.Count == 0 && ProductCode.WithoutSuffix(normal) is { } stem)
        {
            found = Matches(table, stem, wanted);
        }

        if (found.Count == 0)
        {
            foreach (var suffix in ProductCode.Suffixes)
            {
                found.AddRange(Matches(table, $"{normal}-{suffix}", wanted));
            }
        }

        return found.Count == 0
            ? None
            : [.. found
                .GroupBy(m => (m.Title, m.Platform))
                .Select(g => g.First())
                .OrderBy(m => m.Title, StringComparer.Ordinal)
                .ThenBy(m => m.Platform, StringComparer.Ordinal)];
    }

    private static List<GameSerialMatch> Matches(Dictionary<string, GameSerialMatch[]> table, string code, string? platform) =>
        table.TryGetValue(code, out var rows)
            ? [.. rows.Where(r => platform is null || r.Platform == platform)]
            : [];

    private static Dictionary<string, GameSerialMatch[]> Load()
    {
        using var stream = typeof(GameSerialTable).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded game product-code table '{ResourceName}' is missing.");
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress), Encoding.UTF8);

        var grouped = new Dictionary<string, List<GameSerialMatch>>(StringComparer.Ordinal);
        var platforms = new Dictionary<string, string>(StringComparer.Ordinal); // one string per platform, not one per row
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length != 3)
            {
                throw new InvalidOperationException($"A row of the game product-code table is not code, platform and title: '{line}'.");
            }

            var platform = platforms.TryGetValue(fields[1], out var known) ? known : platforms[fields[1]] = fields[1];
            if (!grouped.TryGetValue(fields[0], out var rows))
            {
                grouped[fields[0]] = rows = [];
            }

            rows.Add(new GameSerialMatch(fields[0], fields[2], platform));
        }

        return grouped.ToDictionary(g => g.Key, g => g.Value.ToArray(), StringComparer.Ordinal);
    }
}
