namespace CollectionMaxxing.Application.Common;

/// <summary>The list envelope from docs/04: <c>{ "items": [...], "nextCursor": "..." | null }</c>.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor = null);
