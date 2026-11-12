namespace CollectionMaxxing.Infrastructure.ShelfReading;

/// <summary><c>ShelfReader</c>. An empty <see cref="Endpoint"/> means the reader is off.</summary>
public sealed class ShelfReaderOptions
{
    public const string SectionName = "ShelfReader";

    public const string DefaultPrompt =
        "This is a photo of a shelf of books, video tapes, discs or games. Many spines are sideways or upside down. "
        + "List every item whose title you can read, shelf by shelf, left to right and top to bottom. "
        + "For each item give the title exactly as printed (do not guess or complete a title you cannot read), "
        + "the author or creator if printed on it (else an empty string), and its kind: book, film, tv or game. "
        + "One entry per physical item; repeat an entry if there are two copies.";

    /// <summary>Base address of an OpenAI-compatible server (llama.cpp's llama-server). Empty means off.</summary>
    public string Endpoint { get; set; } = "";

    public string Model { get; set; } = "";

    public string Prompt { get; set; } = DefaultPrompt;

    public int MaxTokens { get; set; } = 3000;

    /// <summary>A cold model load takes about 8 s and the fullest shelf 23 s.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    public int MaxLongEdge { get; set; } = 2048;

    /// <summary>The most times one title is kept when it repeats in a row.</summary>
    public int MaxRepeats { get; set; } = 2;

    /// <summary>
    /// After a first answer that ended normally, ask the model once more for items it has not listed, so an early stop does not lose a shelf.
    /// Both turns together stay inside <see cref="TimeoutSeconds"/>.
    /// </summary>
    public bool SecondLook { get; set; } = true;

    /// <summary>The token cap of the second turn.</summary>
    public int SecondLookMaxTokens { get; set; } = 900;

    /// <summary>True for an absolute http or https address.</summary>
    internal static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    internal static bool IsValid(ShelfReaderOptions o) =>
        (string.IsNullOrWhiteSpace(o.Endpoint) || IsHttpUrl(o.Endpoint))
        && o.MaxTokens > 0 && o.MaxLongEdge > 0 && o.TimeoutSeconds > 0 && o.MaxRepeats > 0 && o.SecondLookMaxTokens > 0;
}
