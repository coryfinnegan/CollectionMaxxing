using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Items;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// What the phone read from one item: text and fields only (never a picture). Every field is a claim to be checked against
/// the catalogue, not a fact; <see cref="Text"/> is whatever was legible, <see cref="Title"/> the model's guess at the title.
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
    string? Platform)
{
    /// <summary>The words to look for: the model's title when it gave one, else everything that was legible. Null when there is nothing to search.</summary>
    public string? SearchText => !string.IsNullOrWhiteSpace(Title) ? Title : !string.IsNullOrWhiteSpace(Text) ? Text : null;

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

        var resolution = TextSearchResolver.Score(facts.ToReadItem(), ProfileFor(profile), local, providers, pendingWhenProvidersFailed: true);
        return resolution with { Steps = [.. resolution.Steps, new TraceStep("profile", profile.ToString(), 0)] };
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
