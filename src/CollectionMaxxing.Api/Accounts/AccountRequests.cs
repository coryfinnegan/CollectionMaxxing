using System.Text.Json;

namespace CollectionMaxxing.Api.Accounts;

/// <summary>Body of <c>PATCH /api/v1/me</c> (JSON Merge Patch, RFC 7396). A property that is absent is left alone.</summary>
public sealed record UpdateMeRequest(string? DisplayName = null, JsonElement? Preferences = null);
