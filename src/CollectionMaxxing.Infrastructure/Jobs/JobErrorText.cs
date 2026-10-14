using System.Text.RegularExpressions;

namespace CollectionMaxxing.Infrastructure.Jobs;

/// <summary>Turns an exception into the short text stored in <c>last_error</c>, which an admin can read: no stack trace, no secrets.</summary>
internal static partial class JobErrorText
{
    private const int MaxLength = 500;

    public static string From(Exception exception)
    {
        var text = $"{exception.GetType().Name}: {exception.Message}";
        text = Credentials().Replace(text, "$1=[redacted]");
        text = Bearer().Replace(text, "Bearer [redacted]");
        text = UrlUserInfo().Replace(text, "://[redacted]@");
        text = text.ReplaceLineEndings(" ");
        return text.Length <= MaxLength ? text : text[..MaxLength] + "...";
    }

    [GeneratedRegex(@"(password|pwd|passwd|secret|token|api[_-]?key|apikey|access[_-]?key|authorization|key)\s*[=:]\s*[^\s;,&""']+", RegexOptions.IgnoreCase)]
    private static partial Regex Credentials();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"://[^/\s@]+@")]
    private static partial Regex UrlUserInfo();
}
