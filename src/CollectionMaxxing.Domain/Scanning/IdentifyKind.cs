namespace CollectionMaxxing.Domain.Scanning;

/// <summary>What an identify read is: a shelf of spines, or one front cover (design §8(b); <c>edition_proof</c> follows in #346).</summary>
public enum IdentifyKind
{
    /// <summary>A shelf segment: spines, or anything else that is not one cover.</summary>
    Shelf,

    /// <summary>One front cover: scored as a cover, answered with the top three and their artwork for tap-to-pick.</summary>
    Cover,
}
