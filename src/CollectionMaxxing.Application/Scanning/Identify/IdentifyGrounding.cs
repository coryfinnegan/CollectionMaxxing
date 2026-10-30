using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Items;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// What the phone read from one item: text and fields only (never a picture). Every field is a claim to be checked against
/// the catalogue, not a fact; <see cref="Text"/> is whatever was legible, <see cref="Title"/> the model's guess at the title.
/// <see cref="ProductCode"/> is set only when the printed product code named exactly one game (ADR-031): that game's title and
/// platform then stand in <see cref="Title"/> and <see cref="Platform"/>, and <see cref="ReadAs"/> keeps the words the read had
/// before (for the trace and for the card's "read as" line). Both are null on every other read.
/// <see cref="KindContext"/> is not part of the read: it is the kind the rest of the snap agreed on (<see cref="Identify.KindContext"/>),
/// set only on a read that names no kind, and it only orders level-scoring releases. It lives here so that it is part of the read's
/// value, which is what the eval run's memo keys on: the same words in a snap of films and in one of books are two lookups.
/// </summary>
public sealed record ReadFacts(
    string? Title,
    string? Text,
    string? Creator,
    string? Edition,
    ItemCompleteness? Completeness,
    WorkKind? Kind,
    ReleaseFormat? Format,
    int? Year,
    string? Platform,
    string? ProductCode = null,
    string? ReadAs = null,
    WorkKind? KindContext = null)
{
    /// <summary>The words to look for: the model's title when it gave one, else everything that was legible. Null when there is nothing to search.</summary>
    public string? SearchText => !string.IsNullOrWhiteSpace(Title) ? Title : !string.IsNullOrWhiteSpace(Text) ? Text : null;

    /// <summary>What to show as the words that were read: the legible text, else the title as read before a product code replaced it, else the title.</summary>
    public string? ShownText => Text ?? ReadAs ?? Title;

    public ReadItem ToReadItem() => new()
    {
        Title = SearchText,
        Kind = Kind,
        Format = Format,
        Platform = Platform,
        Year = Year,
        Creator = Creator,
        Edition = Edition,
    };

    public CatalogQuery? ToQuery() => SearchText is { } text
        ? new CatalogQuery { Title = text, Kind = Kind, Format = Format, Platform = Platform, Year = Year, Creator = Creator, Limit = 10 }
        : null;
}

/// <summary>
/// Grounds a read in the catalogue: the local catalogue first (trigram search), then the providers (whose answers are cached
/// by the provider layer), scored with the profile chosen for the item and the live <see cref="ScoringOptions"/>. This is where
/// "the model never decides a match alone" is enforced: whatever the phone claimed, the verdict comes from what the catalogue
/// and the providers actually hold, through <see cref="MatchScorer"/>'s thresholds. It needs no scan session, so the eval
/// harness can call it directly (<see cref="GroundAsync"/>).
/// </summary>
public sealed class IdentifyGrounding(TextSearchResolver search, IOptionsMonitor<ScoringOptions> scoring)
{
    private static readonly TraceStep NoText = new("reader", "no_text", 0);

    /// <summary>The weights and thresholds in force right now (they can be tuned while the server runs).</summary>
    public ScoringProfile ProfileFor(ScoringProfileKind kind) => scoring.CurrentValue.Profile(kind);

    /// <summary>The database step: what the local catalogue holds for the read. Run one read at a time.</summary>
    public Task<LocalPool> FindLocalAsync(ReadFacts facts, CancellationToken cancellationToken) =>
        facts.SearchText is { } text ? search.FindLocalAsync(text, cancellationToken) : Task.FromResult(LocalPool.Empty);

    /// <summary>The provider step: touches no database, so many reads can run at once within one budget.</summary>
    public async Task<ProviderAnswers> AskProvidersAsync(ReadFacts facts, CancellationToken callerToken, CancellationToken budgetToken) =>
        facts.ToQuery() is { } query ? await search.AskProvidersAsync(query, callerToken, budgetToken) : ProviderAnswers.None;

    /// <summary>
    /// The verdict. A read with nothing legible is NotFound. A read that found nothing good while a provider was down or slow
    /// is Pending, so it is asked again later instead of being called unknown.
    /// </summary>
    public BarcodeResolution Score(ReadFacts facts, ScoringProfileKind profile, LocalPool local, ProviderAnswers providers)
    {
        if (facts.SearchText is null)
        {
            return new BarcodeResolution(ResolutionVerdict.NotFound, [], null, [NoText]);
        }

        var resolution = TextSearchResolver.Score(
            facts.ToReadItem(), ProfileFor(profile), local, providers, pendingWhenProvidersFailed: true, kindContext: facts.KindContext);
        return resolution with
        {
            // A provider film or game names no format: the one the cover was read as is the release's (a DVD, a VHS tape).
            Options = [.. resolution.Options.Select(o => o.Provider is { } p ? o with { Provider = p.WithReadFormat(facts.Format) } : o)],
            Steps = [.. CodeStep(facts), .. resolution.Steps, new TraceStep("profile", profile.ToString(), 0)],
        };
    }

    /// <summary>
    /// The trace line for a read whose product code decided its title (ADR-031): the code, the game it named and the words the
    /// read had before. Nothing for any other read. The step name is <c>product_code</c>, for whoever reads a candidate's trace.
    /// </summary>
    private static IEnumerable<TraceStep> CodeStep(ReadFacts facts)
    {
        if (facts.ProductCode is not { } code)
        {
            yield break;
        }

        var game = facts.Platform is { } platform ? $"{facts.Title} ({platform})" : facts.Title;
        yield return new TraceStep("product_code", facts.ReadAs is { } readAs ? $"{code} = {game}; read as: {readAs}" : $"{code} = {game}", 0);
    }

    /// <summary>The whole grounding for one read, one step after the other, within <paramref name="budget"/> for the providers.</summary>
    public async Task<BarcodeResolution> GroundAsync(ReadFacts facts, ScoringProfileKind profile, TimeSpan budget, CancellationToken cancellationToken)
    {
        if (facts.SearchText is null)
        {
            return Score(facts, profile, LocalPool.Empty, ProviderAnswers.None);
        }

        var local = await FindLocalAsync(facts, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);
        var providers = await AskProvidersAsync(facts, cancellationToken, timeout.Token);
        return Score(facts, profile, local, providers);
    }
}
