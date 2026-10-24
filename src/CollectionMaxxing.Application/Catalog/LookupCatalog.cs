using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Catalog;

/// <summary>
/// Use case: resolve one barcode or catalogue number. Any barcode form is accepted (UPC-A, UPC-E, EAN-8, EAN-13, ISBN-10/13)
/// and normalised first, so a UPC-A and its GTIN-13 form give the same release. The catalogue answers first (global and
/// the caller's own libraries' releases only); the providers are asked only when it has nothing. An unknown code is an
/// empty result, not an error.
/// </summary>
public static class LookupCatalog
{
    public sealed record Query(string? Code);

    public sealed class Handler(
        IAppDbContext db, IEnumerable<ICatalogProvider> providers, BookLookup bookLookup, CatalogOptions options, ILogger<Handler> logger)
    {
        public async Task<Result<CatalogLookupDto>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            if (!Identifier.TryParse(query.Code, out var identifier))
            {
                return CatalogErrors.Invalid("catalog.code_invalid", "That isn't a valid barcode. Check the digits and try again.", "code");
            }

            // 13-digit codes may be stored as an ISBN or as a GTIN; the digits are what identify the product.
            var releaseIds = await (from i in db.ReleaseIdentifiers.AsNoTracking()
                                    join r in db.Releases.AsNoTracking() on i.ReleaseId equals r.Id // the filter hides other libraries'
                                    where i.Value == identifier.Value
                                    select r.Id).Distinct().ToListAsync(cancellationToken);

            var releases = new List<ReleaseDto>();
            foreach (var id in releaseIds.Order())
            {
                if (await ItemProjection.LoadReleaseAsync(db, id, cancellationToken) is { } loaded)
                {
                    releases.Add(ItemProjection.ToReleaseDto(loaded));
                }
            }

            if (releases.Count == 0 && identifier.ProbableKind == WorkKind.Book)
            {
                // Books: the permanent local copy first, then the book providers at once, whichever answers first.
                var book = await bookLookup.LookupAsync(identifier, null, options.ProviderTimeout, cancellationToken);
                if (book.Release is { } found)
                {
                    releases.Add(CatalogProjection.ToReleaseDto(found));
                }
            }
            else if (releases.Count == 0)
            {
                var asked = providers.Where(p => identifier.ProbableKind is not { } kind || p.Supports(kind)).ToList();
                using var failFast = ProviderCalls.FailFast(); // a person is waiting
                var answers = await Task.WhenAll(asked.Select(provider => CatalogProjection.AskProviderAsync(
                    provider, token => provider.LookupAsync(identifier, token), options.ProviderTimeout, logger, cancellationToken)));
                if (answers.FirstOrDefault(a => a is not null) is { } found)
                {
                    releases.Add(CatalogProjection.ToReleaseDto(found));
                }
            }

            return new CatalogLookupDto(identifier.Value, releases);
        }
    }
}
