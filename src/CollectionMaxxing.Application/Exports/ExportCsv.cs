namespace CollectionMaxxing.Application.Exports;

/// <summary>Writes CSV that a spreadsheet can open without running anything the data says.</summary>
public static class ExportCsv
{
    /// <summary>
    /// A cell that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return would be read as a formula by a spreadsheet.
    /// A leading apostrophe makes it plain text. Then commas, quotes and line breaks are quoted (RFC 4180).
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    public static string Row(IEnumerable<string?> cells) => string.Join(',', cells.Select(Cell));
}
