namespace CollectionMaxxing.Domain.Identifiers;

/// <summary>The GS1 mod-10 check digit shared by UPC-A, UPC-E (through its expansion), EAN-8, EAN-13 and ISBN-13.</summary>
internal static class Gs1CheckDigit
{
    /// <summary>True when the last digit is the correct check digit for the digits before it. Digits only.</summary>
    public static bool IsValid(string digits)
    {
        if (digits.Length < 2)
        {
            return false;
        }

        // Counting from the check digit (weight 1) leftwards the weights alternate 3, 1, 3, 1...
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var fromRight = digits.Length - 1 - i;
            sum += (digits[i] - '0') * (fromRight % 2 == 0 ? 1 : 3);
        }

        return sum % 10 == 0;
    }

    /// <summary>The check digit for a body of digits (the body excludes the check digit).</summary>
    public static char For(string body)
    {
        var sum = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var fromRight = body.Length - 1 - i;
            sum += (body[i] - '0') * (fromRight % 2 == 0 ? 3 : 1);
        }

        return (char)('0' + ((10 - (sum % 10)) % 10));
    }
}
