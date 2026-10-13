namespace CollectionMaxxing.Domain.Catalog;

public enum WorkKind
{
    Book,
    Film,
    Tv,
    Game,
    Other,
}

/// <summary>The creative thing itself: the novel, the film, the game. Shared by every user.</summary>
public sealed class Work
{
    public Guid Id { get; init; }

    public WorkKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string SortTitle { get; set; } = string.Empty;

    /// <summary>Lower-cased, accents and punctuation stripped; what the trigram search matches against.</summary>
    public string NormalizedTitle { get; set; } = string.Empty;

    public int? OriginalYear { get; set; }

    /// <summary>JSON array of creators (authors, directors, studios).</summary>
    public string CreatorsJson { get; set; } = "[]";

    public string[] Genres { get; set; } = [];

    public string? Synopsis { get; set; }

    /// <summary>JSON object of provider ids: <c>{ "tmdb": "...", "igdb": "...", "openlibrary_work": "..." }</c>.</summary>
    public string ExternalIdsJson { get; set; } = "{}";

    public Guid? CoverAssetId { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
