using System.Security.Cryptography;
using System.Text;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>What Review changes for one scanned item at commit. A null field means "use the default".</summary>
public sealed record CommitOverride(Guid? CandidateId, Guid? LocationId, ItemStatus? Status, ItemCondition? Condition, int? Copies);

/// <summary>
/// The person's choices at commit (SCAN-1d): "Add to" a location, a status and a condition for everything, and overrides for
/// single items. All optional; no options at all commits exactly as it always did. <c>AcquiredOn</c> is the day the phone is in:
/// the day the items are dated, which beats the session's UTC offset.
/// </summary>
public sealed record CommitOptions(
    Guid? AddTo, ItemStatus? Status, ItemCondition? Condition, IReadOnlyList<CommitOverride>? Overrides, DateOnly? AcquiredOn = null)
{
    public bool IsEmpty => AddTo is null && Status is null && Condition is null && Overrides is not { Count: > 0 };

    /// <summary>
    /// What an <c>Idempotency-Key</c> is tied to: the session alone when there are no options (as before this existed), else the
    /// session and the options, so one key can't be reused for a different commit. The day is left out on purpose: a retry sent
    /// after midnight carries a new day but is the same commit, and must get the first answer back, not a conflict.
    /// </summary>
    public static string Hash(Guid sessionId, CommitOptions? options)
    {
        if (options is null || options.IsEmpty)
        {
            return sessionId.ToString();
        }

        var overrides = (options.Overrides ?? [])
            .OrderBy(o => o.CandidateId)
            .Select(o => $"{o.CandidateId}:{o.LocationId}:{o.Status}:{o.Condition}:{o.Copies}");
        var text = $"{sessionId}|{options.AddTo}|{options.Status}|{options.Condition}|{string.Join(';', overrides)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>
/// Picks the release each accepted candidate is added as. Usually the one it matched. A candidate the person edited (format,
/// platform or edition) is added as the release of the same work that has those: one the catalogue already holds (or the
/// library's own), else a new release the library keeps for itself. Saves nothing; the commit saves everything at once.
/// <see cref="ResolvedReleases.Origins"/> are the matched releases an edit moved away from: the commit still fetches their
/// covers, or the shared release the person saw would stay coverless.
/// </summary>
internal sealed record ResolvedReleases(Dictionary<Guid, Guid> ForCandidate, IReadOnlyList<Guid> Origins);

internal sealed class EditedReleases(IAppDbContext db, IIdGenerator ids)
{
    public async Task<ResolvedReleases> ResolveAsync(
        ScanSession session, IReadOnlyList<ScanCandidate> accepted, DateTimeOffset now, CancellationToken cancellationToken,
        AnswerLookups? lookups = null)
    {
        var result = accepted.ToDictionary(c => c.Id, c => c.MatchedReleaseId!.Value);

        // The format the item was read as (a DVD cover, a VHS sleeve) fills in a matched release the catalogue keeps with
        // format Unknown, which is how every provider film and game is stored. It never overrides a known format.
        var readFormats = accepted
            .Where(c => c.EditedFormat is null && c.Source != CandidateSource.Barcode)
            .Select(c => (c.Id, Format: Identify.StoredRead.From(c).Facts.Format))
            .Where(r => ReleaseFormats.IsKnown(r.Format))
            .ToDictionary(r => r.Id, r => r.Format!.Value);
        var edited = accepted
            .Where(c => c.EditedFormat is not null || c.EditedPlatform is not null || c.EditedEdition is not null || readFormats.ContainsKey(c.Id))
            .ToList();
        if (edited.Count == 0)
        {
            return new ResolvedReleases(result, []);
        }

        var baseIds = edited.Select(c => c.MatchedReleaseId!.Value).Distinct().ToList();
        var bases = lookups is null
            ? await db.Releases.AsNoTracking().Where(r => baseIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken)
            : await lookups.RowsAsync(baseIds, cancellationToken);
        var workIds = bases.Values.Select(r => r.WorkId).Distinct().ToList();
        var kinds = lookups is null
            ? await db.Works.AsNoTracking().Where(w => workIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Kind, cancellationToken)
            : await lookups.KindsAsync(workIds, cancellationToken);
        var platforms = lookups is null ? await db.Platforms.AsNoTracking().ToListAsync(cancellationToken) : await lookups.PlatformsAsync(cancellationToken);
        var added = new Dictionary<(Guid Work, ReleaseFormat Format, Guid? Platform, string Edition), Guid>();
        var origins = new HashSet<Guid>();

        foreach (var candidate in edited)
        {
            var origin = bases[candidate.MatchedReleaseId!.Value];
            var format = candidate.EditedFormat
                ?? (origin.Format == ReleaseFormat.Unknown
                    && readFormats.TryGetValue(candidate.Id, out var read)
                    && kinds.TryGetValue(origin.WorkId, out var kind)
                    && ReleaseFormats.Fits(read, kind)
                        ? read
                        : origin.Format);
            var platformId = PlatformOf(platforms, candidate.EditedPlatform) ?? origin.PlatformId;
            var edition = candidate.EditedEdition ?? origin.Edition;
            if (format == origin.Format && platformId == origin.PlatformId && SameEdition(edition, origin.Edition))
            {
                continue;
            }

            var key = (origin.WorkId, format, platformId, NormalEdition(edition));
            if (!added.TryGetValue(key, out var releaseId))
            {
                releaseId = await FindOrAddAsync(session, origin, format, platformId, edition, now, cancellationToken);
                added[key] = releaseId;
            }

            result[candidate.Id] = releaseId;
            origins.Add(origin.Id);
        }

        return new ResolvedReleases(result, [.. origins]);
    }

    private async Task<Guid> FindOrAddAsync(
        ScanSession session, Release origin, ReleaseFormat format, Guid? platformId, string? edition, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The query filter already hides other libraries' private releases; global ones come first.
        var siblings = await db.Releases.AsNoTracking()
            .Where(r => r.WorkId == origin.WorkId && r.Format == format && r.PlatformId == platformId)
            .OrderBy(r => r.OwnerLibraryId != null).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        if (siblings.FirstOrDefault(r => SameEdition(r.Edition, edition)) is { } found)
        {
            return found.Id;
        }

        var release = new Release
        {
            Id = ids.NewId(),
            WorkId = origin.WorkId,
            Format = format,
            PlatformId = platformId,
            Edition = string.IsNullOrWhiteSpace(edition) ? null : edition.Trim(),
            CoverAssetId = origin.CoverAssetId, // a corrected edition shows the same picture
            Source = ReleaseSource.User,
            OwnerLibraryId = session.LibraryId,
            CreatedBySessionId = session.Id, // so undoing this commit can remove it again
            // The provider's cover address travels with it, so the commit's cover step fetches what the origin hasn't yet.
            ProviderPayloadsJson = origin.CoverAssetId is null ? origin.ProviderPayloadsJson : null,
            CreatedAt = now,
        };
        db.Releases.Add(release);
        return release.Id;
    }

    /// <summary>The platform row a typed platform name means, or null when none does (the release's own platform then stays).</summary>
    private static Guid? PlatformOf(List<Platform> platforms, string? name)
    {
        var canonical = PlatformNames.Canonicalize(name);
        return canonical is null
            ? null
            : platforms.FirstOrDefault(p => PlatformNames.Canonicalize(p.Name) == canonical || PlatformNames.Canonicalize(p.ShortName) == canonical)?.Id;
    }

    private static string NormalEdition(string? edition) => (edition ?? string.Empty).Trim().ToUpperInvariant();

    private static bool SameEdition(string? a, string? b) => NormalEdition(a) == NormalEdition(b);
}
