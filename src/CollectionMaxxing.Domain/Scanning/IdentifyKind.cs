namespace CollectionMaxxing.Domain.Scanning;

/// <summary>What an identify read is: a shelf of spines, one front cover, or proof of an item's edition.</summary>
public enum IdentifyKind
{
    /// <summary>A shelf segment: spines, or anything else that is not one cover.</summary>
    Shelf,

    /// <summary>One front cover: scored as a cover, answered with the top three and their artwork for tap-to-pick.</summary>
    Cover,

    /// <summary>
    /// Cover + copyright page bound to a library item (design §11): a match on the item's own work upgrades its
    /// release link and verifies it; anything else becomes a T2 row and the item is never touched.
    /// </summary>
    EditionProof,
}
