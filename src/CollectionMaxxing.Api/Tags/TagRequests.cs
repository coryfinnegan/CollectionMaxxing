namespace CollectionMaxxing.Api.Tags;

/// <summary>Body of <c>POST …/tags</c>. Names are unique per library, ignoring case.</summary>
public sealed record CreateTagRequest(string? Name = null);
