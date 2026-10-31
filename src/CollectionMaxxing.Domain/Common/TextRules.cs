namespace CollectionMaxxing.Domain.Common;

/// <summary>
/// What free text may contain. Postgres can't store a NUL in text or jsonb, and no other control character belongs in a title, a name
/// or a note; tab and line breaks are allowed, because people type them into notes.
/// </summary>
public static class TextRules
{
    public const string InvalidCharacterCode = "text.invalid_character";

    /// <summary>The validation error to answer with when <paramref name="value"/> holds a forbidden control character; null when it is fine (or absent).</summary>
    public static DomainError? Check(string? value, string field) =>
        HasForbiddenCharacter(value)
            ? new DomainError(ErrorKind.Validation, InvalidCharacterCode, "That can't contain control characters.", field)
            : null;

    private static bool HasForbiddenCharacter(string? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsControl(c) && c is not ('\t' or '\n' or '\r'))
            {
                return true;
            }
        }

        return false;
    }
}
