using System.Text.Json.Nodes;

namespace CollectionMaxxing.Application.Catalog;

/// <summary>
/// What the last cover attempt for a release found, kept in the release's provider payload so "why does this book have
/// no picture?" is answerable from the record: <c>fetched</c>, <c>no-cover</c> (no consulted provider named one) or
/// <c>failed</c> with a <see cref="CoverFailure"/> reason in <c>coverFailure</c>. The payload's other fields are left alone.
/// </summary>
internal static class CoverOutcome
{
    public const string Fetched = "fetched";

    public const string NoCover = "no-cover";

    public const string Failed = "failed";

    /// <summary>The cover address the provider named, if any.</summary>
    public static string? CoverUrlOf(string? payloadJson)
    {
        var url = Text(Parse(payloadJson), "coverUrl");
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    /// <summary>The recorded outcome and failure reason, if any.</summary>
    public static (string? Status, string? Failure) Read(string? payloadJson)
    {
        var payload = Parse(payloadJson);
        return payload is null ? (null, null) : (Text(payload, "coverStatus"), Text(payload, "coverFailure"));
    }

    private static string? Text(JsonObject? payload, string property) =>
        payload?[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>The payload with the outcome recorded. A null failure removes the reason. Null when the payload is no object.</summary>
    public static string? WithOutcome(string? payloadJson, string status, string? failure)
    {
        if (Parse(payloadJson) is not { } payload)
        {
            return null;
        }

        payload["coverStatus"] = status;
        if (failure is null)
        {
            payload.Remove("coverFailure");
        }
        else
        {
            payload["coverFailure"] = failure;
        }

        return payload.ToJsonString();
    }

    /// <summary>The payload with a fresh provider answer's cover address. Missing fields are added; the rest is left alone.</summary>
    public static string WithCover(string? payloadJson, string provider, string externalId, string? coverUrl)
    {
        var payload = Parse(payloadJson) ?? new JsonObject();
        payload["provider"] = provider;
        payload["externalId"] = externalId;
        payload["coverUrl"] = coverUrl;
        return payload.ToJsonString();
    }

    /// <summary>
    /// The payload with only the cover address filled in: the provider, external id, outcome and failure stay
    /// exactly as they were, so a same-novel share (#514) never rewrites whose release this is. Missing objects
    /// are created; anything else is left alone.
    /// </summary>
    public static string WithCoverUrl(string? payloadJson, string coverUrl)
    {
        var payload = Parse(payloadJson) ?? new JsonObject();
        payload["coverUrl"] = coverUrl;
        return payload.ToJsonString();
    }

    private static JsonObject? Parse(string? payloadJson)
    {
        try
        {
            return JsonNode.Parse(payloadJson ?? "null") as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
