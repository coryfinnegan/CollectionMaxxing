using System.Text.Json;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// The commit machinery's item shape, shared by the full commit and the T0 auto-add (docs/16 §6): every item either
/// path creates carries the same fields, so undoing one removes exactly what the other would have made.
/// </summary>
internal static class ScanCommitItems
{
    public static Item Build(
        Guid id,
        Guid libraryId,
        Guid releaseId,
        ItemStatus status,
        ItemCondition? condition,
        ItemCompleteness? completeness,
        Guid? locationId,
        CoverChoice cover,
        Guid? photoId,
        AddedVia via,
        Guid userId,
        DateTimeOffset now,
        DateOnly acquiredOn,
        bool editionVerified = true) => new()
        {
            Id = id,
            LibraryId = libraryId,
            ReleaseId = releaseId,
            Status = status,
            Condition = condition,
            Completeness = completeness,
            LocationId = locationId,
            AcquiredOn = acquiredOn,
            CoverChoice = cover,
            CustomCoverAssetId = photoId,
            AddedVia = via,
            EditionVerified = editionVerified, // false only for a T1 auto-add (docs/16 §6)
            CreatedAt = now,
            CreatedBy = userId, // always the caller
            UpdatedAt = now,
        };

    /// <summary>The completeness the phone read from a picture (loose cartridge, disc only...), if it said.</summary>
    public static ItemCompleteness? CompletenessOf(ScanCandidate candidate)
    {
        if (CandidateJson.ReadElement(candidate.InferredJson) is not { ValueKind: JsonValueKind.Object } inferred
            || !inferred.TryGetProperty("completeness", out var text)
            || text.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        foreach (var value in Enum.GetValues<ItemCompleteness>())
        {
            if (EnumText.ToText(value) == text.GetString())
            {
                return value;
            }
        }

        return null;
    }

    public static AddedVia ViaOf(CandidateSource source) => source switch
    {
        CandidateSource.Barcode => AddedVia.Barcode,
        CandidateSource.Search => AddedVia.Search,
        _ => AddedVia.Picture,
    };
}
