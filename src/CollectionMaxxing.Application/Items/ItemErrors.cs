using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Items;

/// <summary>A result together with the row version it was read at; the API turns the version into an <c>ETag</c>.</summary>
public sealed record Versioned<T>(T Value, uint Version);

public static class ItemErrors
{
    public const string PreconditionFailedCode = "item.precondition_failed";
    public const string KeyReusedCode = "idempotency.key_reused";
    public const string ProviderHitNotFoundCode = "provider_hit.not_found";

    public static readonly DomainError NotFound = new(ErrorKind.NotFound, "item.not_found", "That item doesn't exist.");

    public static readonly DomainError ReleaseNotFound = new(ErrorKind.NotFound, "release.not_found", "That release doesn't exist.");

    /// <summary>The search result an add named is no longer kept (a day passed, or the server never showed it). Searching again shows it afresh.</summary>
    public static readonly DomainError ProviderHitNotFound =
        new(ErrorKind.NotFound, ProviderHitNotFoundCode, "That search result has expired. Search again to add it.");

    public static readonly DomainError LocationNotFound = new(ErrorKind.NotFound, "location.not_found", "That location doesn't exist.");

    public static readonly DomainError TagNotFound = new(ErrorKind.NotFound, "tag.not_found", "One of those tags doesn't exist.");

    public static readonly DomainError PlatformNotFound = new(ErrorKind.NotFound, "platform.not_found", "That platform doesn't exist.");

    public static readonly DomainError RestoreWindowClosed =
        new(ErrorKind.NotFound, "item.restore_window_closed", "That item was deleted more than 30 days ago and can't be restored.");

    /// <summary>412: the caller's <c>If-Match</c> is out of date. The API maps this code to 412, not 409.</summary>
    public static readonly DomainError PreconditionFailed =
        new(ErrorKind.Conflict, PreconditionFailedCode, "This item changed since you loaded it. Reload and try again.");

    /// <summary>422: the same <c>Idempotency-Key</c> was used with a different request. The API maps this code to 422.</summary>
    public static readonly DomainError KeyReused =
        new(ErrorKind.Conflict, KeyReusedCode, "That Idempotency-Key was already used for a different request.");

    public static DomainError Invalid(string code, string message, string field) => new(ErrorKind.Validation, code, message, field);
}

/// <summary>Parses the <c>If-Match</c> header: an ETag is the quoted row version, e.g. <c>"42"</c>.</summary>
public static class ItemVersion
{
    public static bool Matches(string? ifMatch, uint current)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return false;
        }

        foreach (var candidate in ifMatch.Split(','))
        {
            var text = candidate.Trim();
            if (text == "*")
            {
                return true;
            }

            if (text.StartsWith("W/", StringComparison.Ordinal))
            {
                text = text[2..];
            }

            if (uint.TryParse(text.Trim('"'), out var version) && version == current)
            {
                return true;
            }
        }

        return false;
    }
}
