using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Catalog;

/// <summary>
/// Asks the book providers again for a release's cover address, through the same provider list a scan uses. Only books
/// by ISBN are re-asked (a product lookup would spend the daily allowance); anything else is marked as having no cover.
/// Stale kept answers are forgotten first, so the providers are really asked; what they say is kept for good, as in a
/// scan. Shared by the nightly backfill (#496) and the sync scan path (#506): a no-cover verdict only means no
/// <em>consulted</em> provider named one, so both re-verify instead of serving it blindly. Saves nothing; the caller saves.
/// </summary>
public sealed class CoverReResolver(IAppDbContext db, BookLookup books, CatalogOptions options, ScanningOptions scanning)
{
    /// <summary>
    /// Re-resolves one release's cover address, updating the release in place. Returns the steps the re-ask took, so a
    /// scan can show them in its trace; empty when the release has no ISBN to ask by.
    /// </summary>
    public async Task<IReadOnlyList<BookLookupStep>> ReResolveAsync(
        Release release, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var isbn = await IsbnAsync(release.Id, cancellationToken);
        if (isbn is null)
        {
            release.ProviderPayloadsJson = CoverOutcome.WithOutcome(release.ProviderPayloadsJson, CoverOutcome.NoCover, null)
                ?? release.ProviderPayloadsJson;
            release.RefreshedAt = now;
            return [];
        }

        await ForgetStaleAnswersAsync(isbn.Value, cancellationToken);
        var book = await books.LookupAsync(isbn, scanning.BookProviders, options.ProviderTimeout, cancellationToken);
        if (book.Release is { CoverUrl: { } url })
        {
            release.ProviderPayloadsJson =
                CoverOutcome.WithCover(release.ProviderPayloadsJson, book.Release.Provider, book.Release.ExternalId, url);
            return book.Steps;
        }

        // Asked and answered: no provider names a cover. A provider failure answers nothing and is asked again next time.
        release.ProviderPayloadsJson = CoverOutcome.WithOutcome(release.ProviderPayloadsJson, CoverOutcome.NoCover, null)
            ?? release.ProviderPayloadsJson;
        if (!book.Failed)
        {
            release.RefreshedAt = now;
        }

        return book.Steps;
    }

    private async Task<Identifier?> IsbnAsync(Guid releaseId, CancellationToken cancellationToken)
    {
        // Only the shared ISBNs (a provider's or the catalogue's): the answer is written onto a release everyone shares, so a
        // barcode one library taught by hand, right or wrong, must not decide which cover the providers are asked for.
        var values = await db.ReleaseIdentifiers.AsNoTracking()
            .Where(i => i.ReleaseId == releaseId && i.LibraryId == null)
            .Select(i => i.Value)
            .ToListAsync(cancellationToken);
        foreach (var value in values)
        {
            if (Identifier.TryParse(value, out var id) && id.Type == IdentifierType.Isbn13)
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Forgets the kept ISBN answers the re-ask could otherwise serve: the permanent copy and the book providers'
    /// cached ISBN lookups. Only the permanent copy keys rows by the bare ISBN; the providers prefix theirs. The
    /// product lookup is never asked, so its rows need no clearing.
    /// </summary>
    private async Task ForgetStaleAnswersAsync(string isbn13, CancellationToken cancellationToken)
    {
        await db.ProviderCache
            .Where(c => c.Key == isbn13 || c.Key == $"lookup:isbn:{isbn13}")
            .ExecuteDeleteAsync(cancellationToken);
    }
}
