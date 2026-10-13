namespace CollectionMaxxing.Domain.Catalog.Matching;

/// <summary>
/// Scores how well a read matches a release and turns scores into Matched, Ambiguous or NotFound (docs/02 section 6).
/// Pure: no I/O, no clock, no randomness.
/// </summary>
/// <remarks>
/// <para>
/// A term takes part only when the read supplied the datum it compares (a title, a platform, a year...). The score is the
/// weighted mean over the terms that take part, so weights are redistributed over the remaining terms. This is how the art
/// term is dropped when the phone sent no distances, and it also stops a barcode for a film (no platform, no creator)
/// from being capped at 0.80 by terms that cannot apply.
/// </para>
/// <para>
/// When the read supplied a datum but the release does not have it (no year, no platform on file), the term scores a
/// neutral 0.5: it neither counts as agreement nor as a mismatch.
/// </para>
/// </remarks>
public static class MatchScorer
{
    private const double Neutral = 0.5;
    private const int Decimals = 9;

    /// <summary>
    /// The score of one release, from 0 to 1, rounded to nine decimals so that exact boundaries compare exactly. An exact
    /// identifier match is 1.0 whatever the title says. A known platform mismatch caps the score at
    /// <see cref="ScoringThresholds.PlatformMismatchCap"/>.
    /// </summary>
    public static double Score(ReadItem read, CandidateRelease release, ScoringProfile profile)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(profile);

        if (SharesAnIdentifier(read, release))
        {
            return 1.0;
        }

        var weights = profile.Weights;
        var (readTitle, readYear) = SplitTitleAndYear(read);

        double weightSum = 0;
        double weighted = 0;

        void Add(double weight, double similarity)
        {
            if (weight > 0)
            {
                weightSum += weight;
                weighted += weight * similarity;
            }
        }

        if (!string.IsNullOrWhiteSpace(readTitle))
        {
            Add(weights.Title, TitleNormalizer.Similarity(readTitle, release.Title));
        }

        if (read.Format is not null || read.Kind is not null)
        {
            Add(weights.Type, TypeAgreement(read, release));
        }

        var platformMismatch = false;
        if (!string.IsNullOrWhiteSpace(read.Platform))
        {
            var agreement = PlatformAgreement(read.Platform, release.Platform);
            platformMismatch = agreement == 0.0;
            Add(weights.Platform, agreement);
        }

        if (readYear is not null)
        {
            Add(weights.Year, YearProximity(readYear.Value, release.Year));
        }

        if (!string.IsNullOrWhiteSpace(read.Creator))
        {
            Add(weights.Creator, CreatorSimilarity(read.Creator, release.Creators));
        }

        if (!string.IsNullOrWhiteSpace(read.Edition))
        {
            Add(weights.Edition, EditionSimilarity(read.Edition, release.Edition));
        }

        if (VisualSimilarity(read, release) is { } visual)
        {
            Add(weights.Art, visual);
        }

        if (weightSum <= 0)
        {
            return 0.0;
        }

        var score = weighted / weightSum;
        if (platformMismatch)
        {
            score = Math.Min(score, profile.Thresholds.PlatformMismatchCap);
        }

        return Math.Round(Math.Clamp(score, 0.0, 1.0), Decimals);
    }

    /// <summary>
    /// Scores every candidate, orders them best first (ties keep their input order) and decides the outcome from the best
    /// score and its lead over the second. No candidates is NotFound.
    /// </summary>
    public static MatchResult Evaluate(ReadItem read, IReadOnlyList<CandidateRelease> releases, ScoringProfile profile)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(profile);

        var ranked = releases
            .Select(release => new ScoredRelease(release, Score(read, release, profile)))
            .OrderByDescending(scored => scored.Score)
            .ToList();

        if (ranked.Count == 0)
        {
            return new MatchResult(MatchOutcome.NotFound, ranked);
        }

        var outcome = Classify(ranked[0].Score, ranked.Count > 1 ? ranked[1].Score : null, profile.Thresholds);
        return new MatchResult(outcome, ranked);
    }

    /// <summary>
    /// The outcome rules: Matched when the best score is at least <see cref="ScoringThresholds.MatchedScore"/> and its
    /// lead over the runner-up (none counts as unlimited) is at least <see cref="ScoringThresholds.MatchedLead"/>;
    /// Ambiguous when it is at least <see cref="ScoringThresholds.AmbiguousScore"/>; otherwise NotFound.
    /// Values are rounded to nine decimals first, because 0.90 - 0.82 is not exactly 0.08 in binary floating point.
    /// Public so a re-score (for example once artwork distances arrive) can reuse the rules.
    /// </summary>
    public static MatchOutcome Classify(double bestScore, double? runnerUpScore, ScoringThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        var best = Math.Round(bestScore, Decimals);
        var lead = runnerUpScore is { } second ? Math.Round(best - Math.Round(second, Decimals), Decimals) : double.PositiveInfinity;

        if (best >= Math.Round(thresholds.MatchedScore, Decimals) && lead >= Math.Round(thresholds.MatchedLead, Decimals))
        {
            return MatchOutcome.Matched;
        }

        return best >= Math.Round(thresholds.AmbiguousScore, Decimals) ? MatchOutcome.Ambiguous : MatchOutcome.NotFound;
    }

    private static bool SharesAnIdentifier(ReadItem read, CandidateRelease release)
    {
        foreach (var id in read.Identifiers)
        {
            foreach (var other in release.Identifiers)
            {
                if (id == other)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // A year in the read wins; otherwise a trailing year in the title ("Alien 1979") stands in for it.
    private static (string? Title, int? Year) SplitTitleAndYear(ReadItem read)
    {
        if (string.IsNullOrWhiteSpace(read.Title))
        {
            return (null, read.Year);
        }

        if (read.Year is not null)
        {
            return (read.Title, read.Year);
        }

        var (_, year) = TitleNormalizer.SplitTrailingYear(read.Title);
        return (read.Title, year);
    }

    private static double TypeAgreement(ReadItem read, CandidateRelease release)
    {
        if (read.Format is { } readFormat)
        {
            if (release.Format is not { } releaseFormat)
            {
                return Neutral;
            }

            if (readFormat == releaseFormat)
            {
                return 1.0;
            }

            return Family(readFormat) == Family(releaseFormat) && Family(readFormat) != FormatFamily.Other ? 0.5 : 0.0;
        }

        return read.Kind == release.Kind ? 1.0 : 0.0;
    }

    private enum FormatFamily
    {
        Book,
        Video,
        Game,
        Other,
    }

    private static FormatFamily Family(ReleaseFormat format) => format switch
    {
        ReleaseFormat.Hardcover or ReleaseFormat.Paperback or ReleaseFormat.MassMarket => FormatFamily.Book,
        ReleaseFormat.Vhs or ReleaseFormat.Betamax or ReleaseFormat.Laserdisc or ReleaseFormat.Dvd
            or ReleaseFormat.Bluray or ReleaseFormat.UhdBluray => FormatFamily.Video,
        ReleaseFormat.GameCartridge or ReleaseFormat.GameDisc or ReleaseFormat.GameCard => FormatFamily.Game,
        _ => FormatFamily.Other,
    };

    private static double PlatformAgreement(string readPlatform, string? releasePlatform)
    {
        var release = PlatformNames.Canonicalize(releasePlatform);
        if (release is null)
        {
            return Neutral;
        }

        return string.Equals(PlatformNames.Canonicalize(readPlatform), release, StringComparison.Ordinal) ? 1.0 : 0.0;
    }

    // One point for the same year, a quarter less for each year apart, nothing from four years apart.
    private static double YearProximity(int readYear, int? releaseYear) =>
        releaseYear is null ? Neutral : Math.Max(0.0, 1.0 - (0.25 * Math.Abs(readYear - releaseYear.Value)));

    private static double CreatorSimilarity(string readCreator, IReadOnlyList<string> creators)
    {
        if (creators.Count == 0)
        {
            return Neutral;
        }

        return creators.Max(creator => TitleNormalizer.Similarity(readCreator, creator));
    }

    private static double EditionSimilarity(string readEdition, string? releaseEdition) =>
        string.IsNullOrWhiteSpace(releaseEdition) ? Neutral : TitleNormalizer.Similarity(readEdition, releaseEdition);

    // The phone reports a feature-print distance. Treating the print as a unit vector, the cosine similarity is
    // 1 - d^2 / 2: identical pictures (d = 0) give 1, unrelated ones about 0, and anything beyond d = sqrt(2) would be a
    // negative cosine, which is clamped to 0. A missing, negative or non-finite distance means no comparison was made.
    // (The distance scale of Apple's real feature prints is calibrated at the M3 gate; see docs/02.)
    private static double? VisualSimilarity(ReadItem read, CandidateRelease release)
    {
        if (read.ArtDistances is null || !read.ArtDistances.TryGetValue(release.Key, out var distance))
        {
            return null;
        }

        if (!double.IsFinite(distance) || distance < 0)
        {
            return null;
        }

        return Math.Clamp(1.0 - (distance * distance / 2.0), 0.0, 1.0);
    }
}
