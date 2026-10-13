using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Application.Items;

/// <summary>A value in a JSON Merge Patch: either absent (leave alone) or present, possibly <c>null</c> (clear it).</summary>
public readonly record struct PatchValue<T>(bool IsSet, T Value)
{
    public static PatchValue<T> Unset => default;

    public static PatchValue<T> Of(T value) => new(true, value);
}

/// <summary>The fields of an item a PATCH may change. A <c>null</c> status, cover choice or tag list means "leave alone".</summary>
public sealed record ItemPatch
{
    public ItemStatus? Status { get; init; }

    public PatchValue<ItemCondition?> Condition { get; init; }

    public PatchValue<ItemCompleteness?> Completeness { get; init; }

    public PatchValue<Guid?> LocationId { get; init; }

    public PatchValue<string?> Notes { get; init; }

    public PatchValue<DateOnly?> AcquiredOn { get; init; }

    public PatchValue<string?> LentTo { get; init; }

    public PatchValue<DateOnly?> LentOn { get; init; }

    public PatchValue<string?> CustomTitle { get; init; }

    public CoverChoice? CoverChoice { get; init; }

    public IReadOnlyList<Guid>? TagIds { get; init; }
}
