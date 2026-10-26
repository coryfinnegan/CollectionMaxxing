using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Scanning.Provisional;

/// <summary>Maps provisional records to their response shape. <c>IsGuess</c> is true by construction: there is no other value.</summary>
internal static class ProvisionalProjection
{
    public static ProvisionalDto ToDto(ProvisionalRecord p) => new(
        p.Id,
        p.LibraryId,
        p.SessionId,
        p.SnapId,
        p.TitleGuess,
        IsGuess: true,
        p.MediaHint,
        p.LocationId,
        p.Status,
        p.CandidateId,
        ItemProjection.CoverUrl(p.CropAssetId),
        p.CreatedAt,
        p.ResolvedAt,
        p.ResolveAction,
        p.CreatedItemId);
}
