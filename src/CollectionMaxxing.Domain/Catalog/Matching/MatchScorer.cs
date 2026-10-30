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
/// neutral 0.5 when the release is scored on its own (<see cref="Score"/>): it neither counts as agreement nor as a
/// mismatch. In an <see cref="Evaluate"/> run the term stays neutral only while some other candidate can answer it, so a
/// release that confirms the datum still beats one that cannot; a term no candidate can answer is no evidence about any of
/// them and drops out for all, instead of dragging a perfect title and author down toward 0.5. The art term follows the
/// same rule: once any candidate has a distance, one the phone did not compare scores no better than the weakest picture
/// that was compared (and never above neutral), so a coverless candidate can not win on art because the picture matched
/// nothing well (#519).
/// </para>
/// <para>
/// A format read agrees with a release that has no format on file through the release's kind: a DVD with a film or a
/// show, a cartridge with a game, a paperback with a book (providers answer films and games with no format at all).
/// A read kind of Other says nothing, and a platform name the console table does not know can agree with the same name
/// but can never be a mismatch.
/// </para>
/// <para>
/// Four guards keep a wrong release from being auto-added. A year inside both titles must be the same year ("Madden NFL
/// 2005" is not "Madden NFL 2004"), and the year in a title is never taken for the release year. A known platform or
/// title-year conflict caps the score below the Matched threshold. Matched needs at least two signals that were
/// actually compared (both sides present), or an exact barcode match; thinner evidence is at most Ambiguous. And when
/// the title and the kind are the only signals compared, only an exact title (after normalising) can match: the
/// providers asked for a book answer only books, so the kind agrees with every candidate and is no evidence for one.
/// </para>
/// </remarks>
public static class MatchScorer
{
    private const double Neutral = 0.5;
    private const int Decimals = 9;

    /// <summary>A Match needs this many signals compared on both sides, unless a barcode matched exactly.</summary>
    private const int MinimumComparedSignals = 2;

    private const string TitleTerm = "title";
    private const string TypeTerm = "type";

    /// <summary>
    /// Which terms some candidate of the run can answer (so a candidate that can not is neutral on them rather than left
    /// out), and what the art term gives a candidate the phone did not compare (null leaves the term out for it).
    /// </summary>
    private sealed record Answerable(bool Type, bool Platform, bool Year, bool Creator, bool Edition, double? ArtStandIn)
    {
        /// <summary>A release scored on its own: every unanswered term is neutral; art needs a distance of its own.</summary>
        public static Answerable Alone { get; } = new(true, true, true, true, true, null);
    }

    /// <summary>
    /// The score of one release, from 0 to 1, rounded to nine decimals so that exact boundaries compare exactly. An exact
    /// identifier match is 1.0 whatever the title says. A known platform mismatch caps the score at
    /// <see cref="ScoringThresholds.PlatformMismatchCap"/>, and different years in the two titles at
    /// <see cref="ScoringThresholds.TitleYearMismatchCap"/>. This scores the release on its own: the art term is left out
    /// when the read has no distance for it. <see cref="Evaluate"/> scores a whole set and treats a candidate without a
    /// distance as neutral once another has one.
    /// </summary>
    public static double Score(ReadItem read, CandidateRelease release, ScoringProfile profile) =>
        ScoreCore(read, release, profile, Answerable.Alone).Score;

    private static ScoredRelease ScoreCore(ReadItem read, CandidateRelease release, ScoringProfile profile, Answerable answerable)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(profile);

        if (SharesAnIdentifier(read, release))
        {
            return new ScoredRelease(release, 1.0) { IsExactIdentifierMatch = true, Terms = [new ScoreTerm("identifier", 1.0, 1.0, true)] };
        }

        var weights = profile.Weights;

        double weightSum = 0;
        double weighted = 0;
        var compared = 0;
        var terms = new List<ScoreTerm>();

        // A term takes part when its weight is positive. A similarity is a compared signal: the release could answer it
        // too. Null means the release could not: the term is then neutral if another candidate can answer it (no
        // evidence either way for this one), else it is left out (no evidence about anyone).
        void Add(string name, double weight, double? similarity, bool othersAnswer, double neutral = Neutral)
        {
            if (weight <= 0)
            {
                return;
            }

            if (similarity is { } value)
            {
                weightSum += weight;
                weighted += weight * value;
                compared++;
                terms.Add(new ScoreTerm(name, weight, value, true));
            }
            else if (othersAnswer)
            {
                weightSum += weight;
                weighted += weight * neutral;
                terms.Add(new ScoreTerm(name, weight, neutral, false));
            }
        }

        var titleYearConflict = false;
        var creatorInTitle = false;
        if (!string.IsNullOrWhiteSpace(read.Title))
        {
            if (!string.IsNullOrWhiteSpace(release.Title))
            {
                // A spine reads title and author as one line: this release's own author, found in the read, is not part of
                // its title, and finding it there is the creator signal a separate author field would have been.
                var similarity = TitleNormalizer.Similarity(read.Title, release.Title);
                foreach (var creator in release.Creators)
                {
                    if (TitleNormalizer.WithoutCreator(read.Title, creator) is { } bare)
                    {
                        creatorInTitle = true;
                        similarity = Math.Max(similarity, TitleNormalizer.Similarity(bare, release.Title));
                    }
                }

                Add(TitleTerm, weights.Title, similarity, othersAnswer: true);
            }
            else if (weights.Title > 0)
            {
                weightSum += weights.Title; // a release with no title scores nothing on it, and it is not a compared signal
                terms.Add(new ScoreTerm(TitleTerm, weights.Title, 0.0, false));
            }

            titleYearConflict = TitleNormalizer.TitleYearsConflict(read.Title, release.Title);
        }

        if (read.Format is not null || read.Kind is not null)
        {
            Add(TypeTerm, weights.Type, TypeAgreement(read, release), answerable.Type);
        }

        var platformMismatch = false;
        if (!string.IsNullOrWhiteSpace(read.Platform))
        {
            var agreement = PlatformAgreement(read.Platform, release.Platform);
            platformMismatch = agreement == 0.0;
            Add("platform", weights.Platform, agreement, answerable.Platform);
        }

        // Only a year that was read as a year: the year inside a title belongs to the title (see TitleNormalizer).
        if (read.Year is { } readYear)
        {
            Add("year", weights.Year, release.Year is { } year ? YearProximity(readYear, year) : null, answerable.Year);
        }

        if (!string.IsNullOrWhiteSpace(read.Creator))
        {
            // Two pieces of evidence, the better one counts: the author the reader named, and this release's own author
            // found printed in the title line. A spine's smaller print is often misread as the author (a fragment, the
            // publisher), and that must not hide the author the title line carries.
            double? similarity = release.Creators.Count > 0 ? CreatorSimilarity(read.Creator, release.Creators) : null;
            if (creatorInTitle)
            {
                similarity = Math.Max(similarity ?? 0.0, 1.0);
            }

            Add("creator", weights.Creator, similarity, answerable.Creator);
        }
        else if (creatorInTitle)
        {
            Add("creator", weights.Creator, 1.0, othersAnswer: true);
        }

        if (!string.IsNullOrWhiteSpace(read.Edition))
        {
            Add(
                "edition",
                weights.Edition,
                string.IsNullOrWhiteSpace(release.Edition) ? null : TitleNormalizer.Similarity(read.Edition, release.Edition),
                answerable.Edition);
        }

        if (VisualSimilarity(read, release) is { } visual)
        {
            Add("art", weights.Art, visual, othersAnswer: true);
        }
        else if (answerable.ArtStandIn is { } standIn)
        {
            Add("art", weights.Art, null, othersAnswer: true, neutral: standIn);
        }

        if (weightSum <= 0)
        {
            return new ScoredRelease(release, 0.0) { Terms = terms };
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
            Terms = terms,
        };
    }

    /// <summary>
    /// Scores every candidate, orders them best first (ties keep their input order) and decides the outcome from the best
    /// score and its lead over the second. No candidates is NotFound. A best score that rests on fewer than two compared
    /// signals (and is not an exact barcode match), on a known conflict, or on a near title with only the kind compared
    /// besides it, is at most Ambiguous.
    /// </summary>
    public static MatchResult Evaluate(ReadItem read, IReadOnlyList<CandidateRelease> releases, ScoringProfile profile)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(profile);

        var answerable = AnswerableIn(read, releases);
        var ranked = releases
            .Select(release => ScoreCore(read, release, profile, answerable))
            .OrderByDescending(scored => scored.Score)
            .ToList();

        if (ranked.Count == 0)
        {
            return new MatchResult(MatchOutcome.NotFound, ranked);
        }

        var best = ranked[0];
        var outcome = Classify(best.Score, ranked.Count > 1 ? ranked[1].Score : null, profile.Thresholds);
        if (outcome == MatchOutcome.Matched
            && !best.IsExactIdentifierMatch
            && (best.ComparedSignals < MinimumComparedSignals || best.HasKnownConflict || RestsOnANearTitleAlone(best)))
        {
            outcome = MatchOutcome.Ambiguous;
        }

        return new MatchResult(outcome, ranked);
    }

    /// <summary>
    /// The score rests on a title that is close but not the same, with nothing compared besides it and the kind (no author,
    /// year, platform, edition or picture): in the sixth eval run a made-up spine read matched an old pamphlet with a
    /// near title at 0.94, as "lantern road dinners" would match "A Lantern Road Dinner".
    /// </summary>
    private static bool RestsOnANearTitleAlone(ScoredRelease best) =>
        best.Terms.Where(t => t.Compared).All(t => t.Name is TitleTerm or TypeTerm)
        && best.Terms.FirstOrDefault(t => t.Name == TitleTerm) is { } title
        && Math.Round(title.Value, Decimals) < 1.0;

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

    /// <summary>
    /// Which terms at least one candidate can answer, and the art term's value for a candidate the phone did not compare:
    /// the weakest compared picture, never above neutral (null when no candidate was compared).
    /// </summary>
    private static Answerable AnswerableIn(ReadItem read, IReadOnlyList<CandidateRelease> releases)
    {
        var visuals = releases.Select(release => VisualSimilarity(read, release)).OfType<double>().ToList();
        return new Answerable(
            Type: releases.Any(release => TypeAgreement(read, release) is not null),
            Platform: !string.IsNullOrWhiteSpace(read.Platform) && releases.Any(release => PlatformAgreement(read.Platform, release.Platform) is not null),
            Year: releases.Any(release => release.Year is not null),
            Creator: releases.Any(release => release.Creators.Count > 0),
            Edition: releases.Any(release => !string.IsNullOrWhiteSpace(release.Edition)),
            ArtStandIn: visuals.Count > 0 ? Math.Min(Neutral, visuals.Min()) : null);
    }

    // The type term, or null when it can not set two real values against each other: a format against the release's
    // format, else against the release's kind; a kind against a kind when neither says Other (which is also the
    // "unknown" default of a candidate that has no kind).
    private static double? TypeAgreement(ReadItem read, CandidateRelease release)
    {
        // Unknown is the catalogue saying it has no format on file (every provider film and game is stored so), not a format.
        if (read.Format is { } readFormat && readFormat != ReleaseFormat.Unknown)
        {
            if (release.Format is not { } releaseFormat || releaseFormat == ReleaseFormat.Unknown)
            {
                return KindAgreement(Family(readFormat), release.Kind);
            }

            if (readFormat == releaseFormat)
            {
                return 1.0;
            }

            return Family(readFormat) == Family(releaseFormat) && Family(readFormat) != FormatFamily.Other ? 0.5 : 0.0;
        }

        if (read.Kind is not { } kind || kind == WorkKind.Other || release.Kind == WorkKind.Other)
        {
            return null;
        }

        return kind == release.Kind ? 1.0 : 0.0;
    }

    // A release with no format on file still has a kind: providers answer films, shows and games with none.
    private static double? KindAgreement(FormatFamily family, WorkKind kind)
    {
        if (family == FormatFamily.Other || kind == WorkKind.Other)
        {
            return null;
        }

        var agrees = family switch
        {
            FormatFamily.Book => kind == WorkKind.Book,
            FormatFamily.Video => kind is WorkKind.Film or WorkKind.Tv,
            _ => kind == WorkKind.Game,
        };
        return agrees ? 1.0 : 0.0;
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

    // Null when the release names no platform, or when one of the two names is not a console the table knows and they
    // differ: "boxed" or a typo agrees with nothing, but it is no proof of the wrong console either.
    private static double? PlatformAgreement(string? readPlatform, string? releasePlatform)
    {
        var release = PlatformNames.Canonicalize(releasePlatform);
        if (release is null)
        {
            return null;
        }

        if (string.Equals(PlatformNames.Canonicalize(readPlatform), release, StringComparison.Ordinal))
        {
            return 1.0;
        }

        return PlatformNames.IsKnown(readPlatform) && PlatformNames.IsKnown(releasePlatform) ? 0.0 : null;
    }

    // One point for the same year, a quarter less for each year apart, nothing from four years apart.
    private static double YearProximity(int readYear, int releaseYear) =>
        Math.Max(0.0, 1.0 - (0.25 * Math.Abs(readYear - releaseYear)));

    private static double CreatorSimilarity(string readCreator, IReadOnlyList<string> creators) =>
        creators.Max(creator => TitleNormalizer.Similarity(readCreator, creator));

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
