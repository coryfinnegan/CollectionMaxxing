using System.Text.Json;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Scanning.Artwork;
using CollectionMaxxing.Application.Scanning.Quota;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// What is stored in a candidate's <c>trace</c>: the steps taken and where the library already has the release. Never a code or
/// a key. <paramref name="NeedsArt"/> is the close-call flag (SCAN-2b), <paramref name="AutoDecision"/> the decision the
/// server made by itself (so that a person's own decision can be told from it), and <paramref name="RetryAfter"/> when a code
/// that found the UPC allowance spent will next be tried.
/// </summary>
internal sealed record StoredTrace(
    IReadOnlyList<TraceStep> Steps,
    IReadOnlyList<string> OwnedAt,
    bool NeedsArt = false,
    CandidateDecision? AutoDecision = null,
    DateTimeOffset? RetryAfter = null);

/// <summary>One entry of a candidate's <c>alternatives</c>: a release the person can pick, with its own ownership.</summary>
public sealed record StoredAlternative(
    Guid ReleaseId,
    string Title,
    string Format,
    string? Platform,
    int? Year,
    double Score,
    IReadOnlyList<string> Creators,
    string? CoverUrl,
    int AlreadyOwnedCount,
    string? ArtworkUrl = null);

internal static class CandidateJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static StoredTrace ReadTrace(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new StoredTrace([], []);
        }

        try
        {
            return JsonSerializer.Deserialize<StoredTrace>(json, Options) ?? new StoredTrace([], []);
        }
        catch (JsonException)
        {
            return new StoredTrace([], []);
        }
    }

    public static IReadOnlyList<StoredAlternative> ReadAlternatives(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<StoredAlternative>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static JsonElement? ReadElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Maps candidates to their response shape, loading the releases they point at in a few queries.</summary>
internal static class CandidateProjection
{
    public static async Task<List<CandidateDto>> ToDtosAsync(
        IAppDbContext db, IReadOnlyList<ScanCandidate> candidates, CancellationToken cancellationToken)
    {
        var releaseIds = candidates.Select(c => c.MatchedReleaseId).OfType<Guid>().Distinct().ToList();
        var releases = await LoadReleasesAsync(db, releaseIds, cancellationToken);
        var art = await ArtworkLinks.ForCloseCallsAsync(db, candidates, cancellationToken);
        return candidates.Select(c => ToDto(c, c.MatchedReleaseId is { } id && releases.TryGetValue(id, out var release) ? release : null, art)).ToList();
    }

    public static CandidateDto ToDto(ScanCandidate c, ReleaseDto? release, IReadOnlyDictionary<Guid, string>? artwork = null)
    {
        var trace = CandidateJson.ReadTrace(c.TraceJson);
        var needsArt = ArtworkLinks.AsksForArt(c, trace);
        var waitingForQuota = c.Resolution == CandidateResolution.Pending && UpcQuotaWindow.IsSpent(trace.Steps);
        var edits = c.EditedFormat is null && c.EditedPlatform is null && c.EditedEdition is null
            ? null
            : new CandidateEditsDto(c.EditedFormat, c.EditedPlatform, c.EditedEdition);
        var alternatives = AlternativesOf(c.AlternativesJson, needsArt ? artwork : null);
        return ToDto(c, release, trace, needsArt, waitingForQuota, edits, alternatives);
    }

    /// <summary>The stored alternatives as the response lists them; null when the candidate has none stored. A close call gets the artwork link of each.</summary>
    private static List<CandidateAlternativeDto>? AlternativesOf(string? alternativesJson, IReadOnlyDictionary<Guid, string>? artwork)
    {
        if (string.IsNullOrWhiteSpace(alternativesJson))
        {
            return null;
        }

        return CandidateJson.ReadAlternatives(alternativesJson)
            .Select(a => new CandidateAlternativeDto(
                a.ReleaseId, TitleCaser.ToTitleCase(a.Title), a.Format, a.Platform, a.Year, a.Score, a.Creators, a.CoverUrl,
                a.AlreadyOwnedCount, artwork is null ? a.ArtworkUrl : artwork.GetValueOrDefault(a.ReleaseId)))
            .ToList();
    }

    private static CandidateDto ToDto(
        ScanCandidate c, ReleaseDto? release, StoredTrace trace, bool needsArt, bool waitingForQuota, CandidateEditsDto? edits, IReadOnlyList<CandidateAlternativeDto>? alternatives) => new(
        c.Id,
        c.Seq,
        c.Source,
        c.RawCode,
        c.Symbology,
        c.VisibleText,
        c.LocalId,
        c.BoxX is { } x && c.BoxY is { } y && c.BoxW is { } w && c.BoxH is { } h ? new BoxDto(x, y, w, h) : null,
        c.BoxSource,
        c.Resolution,
        c.Score,
        c.ArtDistance,
        needsArt,
        release,
        CandidateJson.ReadElement(c.InferredJson),
        alternatives,
        c.Decision,
        c.Copies,
        c.AlreadyOwnedCount,
        trace.OwnedAt,
        c.CreatedItemId,
        c.ChangeSeq,
        edits,
        c.UserEdited,
        c.Resolution == CandidateResolution.Pending ? (waitingForQuota ? PendingReason.Quota : PendingReason.Lookup) : null,
        waitingForQuota ? trace.RetryAfter : null,
        c.ShotKind,
        c.SegmentId,
        c.GuidanceCode);

    /// <summary>The releases the caller may see, by id. One that is hidden (another library's private release) is simply absent.</summary>
    public static async Task<Dictionary<Guid, ReleaseDto>> LoadReleasesAsync(
        IAppDbContext db, IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        if (releaseIds.Count == 0)
        {
            return [];
        }

        var releases = await db.Releases.AsNoTracking().Where(r => releaseIds.Contains(r.Id)).ToListAsync(cancellationToken);
        var workIds = releases.Select(r => r.WorkId).Distinct().ToList();
        var works = await db.Works.AsNoTracking().Where(w => workIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);
        var platformIds = releases.Select(r => r.PlatformId).OfType<Guid>().Distinct().ToList();
        var platforms = platformIds.Count == 0
            ? []
            : await db.Platforms.AsNoTracking().Where(p => platformIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var identifiers = (await db.ReleaseIdentifiers.AsNoTracking()
                .Where(i => releaseIds.Contains(i.ReleaseId))
                .OrderBy(i => i.Type).ThenBy(i => i.Value)
                .ToListAsync(cancellationToken))
            .ToLookup(i => i.ReleaseId);

        return releases.ToDictionary(
            r => r.Id,
            r => ItemProjection.ToReleaseDto(new LoadedRelease(
                r,
                works[r.WorkId],
                r.PlatformId is { } platformId && platforms.TryGetValue(platformId, out var platform) ? platform : null,
                identifiers[r.Id].Select(i => new ReleaseIdentifierDto(i.Type, i.Value)).ToList())));
    }

    public static StoredAlternative AlternativeOf(ReleaseDto release, double score, int alreadyOwnedCount) => new(
        release.Id, release.Title, EnumText.ToText(release.Format), release.Platform?.Name, release.Year, score, release.Creators,
        release.CoverUrl, alreadyOwnedCount);
}
