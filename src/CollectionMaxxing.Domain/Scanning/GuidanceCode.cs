using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Domain.Scanning;

/// <summary>
/// What an unresolved row tells the person to do next (design §8(a)): reshoot guidance from the phone's classifier, or the
/// next fallback when the phone sent no signal. Recorded per candidate so evals can audit fallback usage. A resolved row
/// carries none. The API spells these the same, snake_cased.
/// </summary>
public enum GuidanceCode
{
    /// <summary>Move closer — spines too small.</summary>
    MoveCloser,

    /// <summary>Split the segment — shoot left/right halves.</summary>
    SplitSegment,

    /// <summary>Hold steady — add light.</summary>
    HoldSteady,

    /// <summary>Tilt to kill the glare.</summary>
    KillGlare,

    /// <summary>Shoot the front cover (fallback (b) is the way out).</summary>
    ShootCover,

    /// <summary>Pick the right one from the alternatives.</summary>
    PickMatch,

    /// <summary>Scan the barcode again.</summary>
    ScanBarcode,
}

/// <summary>
/// The guidance map (design §8(a)): the classifier's signal plus the engine's <see cref="FallbackReason"/> decide the copy.
/// Pure, so the resolver, the identify path and the jobs all agree.
/// </summary>
public static class GuidanceCodes
{
    /// <summary>
    /// The guidance for a row: a reshoot signal wins while the row is unresolved (except Ambiguous, whose alternatives are
    /// the guidance); otherwise the resolution and the source say what to do next. A still-looking-up row says nothing —
    /// "Identifying…" is already the truth — but a quota wait points at the cover.
    /// </summary>
    public static GuidanceCode? For(
        FallbackReason? signal, CandidateResolution resolution, CandidateSource source, string? rawCode, bool quotaSpent)
    {
        if (resolution == CandidateResolution.Matched)
        {
            return null;
        }

        if (resolution == CandidateResolution.Ambiguous)
        {
            return GuidanceCode.PickMatch;
        }

        if (signal is FallbackReason.MoveCloser or FallbackReason.SplitSegment or FallbackReason.HoldSteady or FallbackReason.KillGlare)
        {
            return signal switch
            {
                FallbackReason.MoveCloser => GuidanceCode.MoveCloser,
                FallbackReason.SplitSegment => GuidanceCode.SplitSegment,
                FallbackReason.HoldSteady => GuidanceCode.HoldSteady,
                _ => GuidanceCode.KillGlare,
            };
        }

        if (resolution == CandidateResolution.Pending)
        {
            return quotaSpent ? GuidanceCode.ShootCover : null;
        }

        if (resolution == CandidateResolution.Error && source == CandidateSource.Barcode && !Identifier.TryParse(rawCode, out _))
        {
            return GuidanceCode.ScanBarcode;
        }

        return GuidanceCode.ShootCover;
    }

    /// <summary>Whether the code came from the phone's classifier (sticky across re-resolves) rather than being derived.</summary>
    public static bool IsReshoot(GuidanceCode code) => code is
        GuidanceCode.MoveCloser or GuidanceCode.SplitSegment or GuidanceCode.HoldSteady or GuidanceCode.KillGlare;
}
