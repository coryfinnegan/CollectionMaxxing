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
/// neutral 0.5: it neither counts as agreement nor as a mismatch. The art term follows the same rule once any candidate
/// of an <see cref="Evaluate"/> run has a distance: a candidate the phone did not compare is neutral on art, so it can
/// not beat a compared one just because the art term was dropped for it alone.
/// </para>
/// <para>
/// Three guards keep a wrong release from being auto-added. A year inside both titles must be the same year ("Madden NFL
/// 2005" is not "Madden NFL 2004"), and the year in a title is never taken for the release year. A known platform or
/// title-year conflict caps the score below the Matched threshold. And Matched needs at least two signals that were
/// actually compared (both sides present), or an exact barcode match; thinner evidence is at most Ambiguous.
/// </para>
/// </remarks>
public static class MatchScorer
{
    private const double Neutral = 0.5;
    private const int Decimals = 9;

    /// <summary>A Match needs this many signals compared on both sides, unless a barcode matched exactly.</summary>
    private const int MinimumComparedSignals = 2;

    /// <summary>
    /// The score of one release, from 0 to 1, rounded to nine decimals so that exact boundaries compare exactly. An exact
    /// identifier match is 1.0 whatever the title says. A known platform mismatch caps the score at
    /// <see cref="ScoringThresholds.PlatformMismatchCap"/>, and different years in the two titles at
    /// <see cref="ScoringThresholds.TitleYearMismatchCap"/>. This scores the release on its own: the art term is left out
    /// when the read has no distance for it. <see cref="Evaluate"/> scores a whole set and treats a candidate without a
    /// distance as neutral once another has one.
    /// </summary>
    public static double Score(ReadItem read, CandidateRelease release, ScoringProfile profile) =>
        ScoreCore(read, release, profile, otherCandidatesWereCompared: false).Score;

    private static ScoredRelease ScoreCore(ReadItem read, CandidateRelease release, ScoringProfile profile, bool otherCandidatesWereCompared)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(profile);

        if (SharesAnIdentifier(read, release))
        {
            return new ScoredRelease(release, 1.0) { IsExactIdentifierMatch = true };
        }

        var weights = profile.Weights;

        double weightSum = 0;
        double weighted = 0;
        var compared = 0;

        // A term takes part when its weight is positive. It counts as a compared signal only when the release could
        // answer it too; a neutral score is no evidence either way.
        void Add(double weight, double similarity, bool bothSidesPresent)
        {
            if (weight > 0)
            {
                weightSum += weight;
                weighted += weight * similarity;
                if (bothSidesPresent)
                {
                    compared++;
                }
            }
        }

        var titleYearConflict = false;
        if (!string.IsNullOrWhiteSpace(read.Title))
        {
            Add(weights.Title, TitleNormalizer.Similarity(read.Title, release.Title), !string.IsNullOrWhiteSpace(release.Title));
            titleYearConflict = TitleNormalizer.TitleYearsConflict(read.Title, release.Title);
        }

        if (read.Format is not null || read.Kind is not null)
        {
            Add(weights.Type, TypeAgreement(read, release), TypeIsCompared(read, release));
        }

        var platformMismatch = false;
        if (!string.IsNullOrWhiteSpace(read.Platform))
        {
            var agreement = PlatformAgreement(read.Platform, release.Platform);
            platformMismatch = agreement == 0.0;
            Add(weights.Platform, agreement, PlatformNames.Canonicalize(release.Platform) is not null);
        }

        // Only a year that was read as a year: the year inside a title belongs to the title (see TitleNormalizer).
        if (read.Year is { } readYear)
        {
            Add(weights.Year, YearProximity(readYear, release.Year), release.Year is not null);
        }

        if (!string.IsNullOrWhiteSpace(read.Creator))
        {
            Add(weights.Creator, CreatorSimilarity(read.Creator, release.Creators), release.Creators.Count > 0);
        }

        if (!string.IsNullOrWhiteSpace(read.Edition))
        {
            Add(weights.Edition, EditionSimilarity(read.Edition, release.Edition), !string.IsNullOrWhiteSpace(release.Edition));
        }

        if (VisualSimilarity(read, release) is { } visual)
        {
            Add(weights.Art, visual, bothSidesPresent: true);
        }
        else if (otherCandidatesWereCompared)
        {
            Add(weights.Art, Neutral, bothSidesPresent: false);
        }

        if (weightSum <= 0)
        {
            return new ScoredRelease(release, 0.0);
        }

        var score = weighted / weightSum;
        if (platformMismatch)
        {
            score = Math.Min(score, profile.Thresholds.PlatformMismatchCap);
        }

        if (titleYearConflict)
        {
            score = Math.Min(score, profile.Thresholds.TitleYearMismatchCap);
        }

        return new ScoredRelease(release, Math.Round(Math.Clamp(score, 0.0, 1.0), Decimals))
        {
            ComparedSignals = compared,
            HasKnownConflict = platformMismatch || titleYearConflict,
        };
    }

    /// <summary>
    /// Scores every candidate, orders them best first (ties keep their input order) and decides the outcome from the best
    /// score and its lead over the second. No candidates is NotFound. A best score that rests on fewer than two compared
    /// signals (and is not an exact barcode match), or on a known conflict, is at most Ambiguous.
    /// </summary>
    public static MatchResult Evaluate(ReadItem read, IReadOnlyList<CandidateRelease> releases, ScoringProfile profile)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(profile);

        // Once the phone compared the picture with any candidate, one it did not compare is neutral, not exempt.
        var anyArtCompared = releases.Any(release => VisualSimilarity(read, release) is not null);

        var ranked = releases
            .Select(release => ScoreCore(read, release, profile, anyArtCompared))
            .OrderByDescending(scored => scored.Score)
            .ToList();

        if (ranked.Count == 0)
        {
            return new MatchResult(MatchOutcome.NotFound, ranked);
        }

        var best = ranked[0];
        var outcome = Classify(best.Score, ranked.Count > 1 ? ranked[1].Score : null, profile.Thresholds);
        if (outcome == MatchOutcome.Matched && !best.IsExactIdentifierMatch && (best.ComparedSignals < MinimumComparedSignals || best.HasKnownConflict))
        {
            outcome = MatchOutcome.Ambiguous;
        }

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

    // Whether the type term set two real values against each other. A format is compared when the release has one; a kind
    // when neither side says Other, which is also the "unknown" default of a candidate that has no kind.
    private static bool TypeIsCompared(ReadItem read, CandidateRelease release) =>
        read.Format is not null
            ? release.Format is not null
            : read.Kind is { } kind && kind != WorkKind.Other && release.Kind != WorkKind.Other;

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
        ReleaseFormat.Hardcover or ReleaseFormat.Paperback or ReleaseFormat.MassMarket or ReleaseFormat.BoardBook
            or ReleaseFormat.SpiralBound => FormatFamily.Book,
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
