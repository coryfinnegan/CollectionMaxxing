using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// Who may compare a run with the baseline when the knobs moved (docs/16 §17): the run's threshold fingerprint, the
/// baseline's, and the note the run carries when they differ on purpose. A mismatch without a note is not a pass.
/// </summary>
public sealed record ThresholdAttestation(string RunHash, string? BaselineHash, string? BaselineNote);

/// <summary>
/// The §16 thresholds as one fingerprint, hashed into every run's manifest. A threshold move without a same-PR baseline
/// note and acceptance re-run shows up as a hash change, and the regression check calls it <c>incomplete</c>.
/// The phone-side knobs (barcode stability, auto-snap gate) are not in here: the server cannot audit them, so an app
/// release carries them under a new <c>configVersion</c> instead.
/// </summary>
public static class EvalThresholds
{
    /// <summary>How many hex characters of the SHA-256 are kept: change detection, not security.</summary>
    public const int HashLength = 16;

    /// <summary>
    /// The version of the T0/T1/T2 assignment rules (<c>CandidateResolver.EvaluateTier</c> plus the series-volume veto and the
    /// loose-item rule). Logic cannot be hashed, so a rule change must bump this by hand; the pinned fingerprint test fails until it does.
    /// </summary>
    public const string TierRulesVersion = "t1-v1";

    /// <summary>
    /// The session default for <c>autoCommitTiers</c> (<c>ScanSessionUseCases</c>). A default only the code knows is hashed as a
    /// marker, for the same reason as <see cref="TierRulesVersion"/>.
    /// </summary>
    public const string DefaultAutoCommitTiers = "T0";

    /// <summary>The fingerprint of the effective thresholds: the live scoring rules plus the gate bars and the regression band.</summary>
    public static string Fingerprint(ScoringThresholds scoring)
    {
        var lines = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["ambiguous_score"] = Number(scoring.AmbiguousScore),
            ["auto_add_precision_min"] = Number(M3Gate.MinAutoAddPrecisionPct),
            ["autocommit.default_tiers"] = DefaultAutoCommitTiers,
            ["gate.max_invented_pct"] = Number(M3Gate.MaxInventedPct),
            ["gate.max_refusal_pct"] = Number(M3Gate.MaxRefusalPct),
            ["gate.shelf_first_item_p50_ms"] = Number(M3Gate.ShelfFirstItemMs),
            ["gate.shelf_total_p50_ms"] = Number(M3Gate.ShelfTotalMs),
            ["gate.single_p50_ms"] = Number(M3Gate.SingleP50Ms),
            ["matched_lead"] = Number(scoring.MatchedLead),
            ["matched_score"] = Number(scoring.MatchedScore),
            ["platform_mismatch_cap"] = Number(scoring.PlatformMismatchCap),
            ["regression.allowed_points"] = Number(EvalRegression.AllowedPoints),
            ["tier.rules"] = TierRulesVersion,
            ["title_year_mismatch_cap"] = Number(scoring.TitleYearMismatchCap),
        };
        var canonical = string.Join('\n', lines.Select(line => line.Key + "=" + line.Value)) + "\n";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..HashLength];
    }

    private static string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
