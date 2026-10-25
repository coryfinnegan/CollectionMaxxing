using System.Data.Common;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Infrastructure.Persistence;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace CollectionMaxxing.Application.Tests.Catalog;

// Issue #292: releases sharing a barcode load as a set, not one query at a time.
public class LookupCatalogTests(PostgresFixture db) : DatabaseTest(db)
{
    private const string Barcode = "0045496870010"; // a non-book GTIN-13, as in the API catalogue tests

    private async Task<List<Guid>> SeedReleasesSharingBarcodeAsync(int count)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var platformId = Guid.CreateVersion7();
        await using (var context = Db.CreateContext())
        {
            context.Platforms.Add(new Platform { Id = platformId, Name = "Test Console", ShortName = "TC" });
            await context.SaveChangesAsync();
        }

        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            await using var context = Db.CreateContext();
            var work = new Work
            {
                Id = Guid.CreateVersion7(),
                Kind = WorkKind.Film,
                Title = $"Film {i}",
                SortTitle = $"Film {i}",
                NormalizedTitle = $"film {i}",
                CreatedAt = now,
            };
            var release = new Release
            {
                Id = Guid.CreateVersion7(),
                WorkId = work.Id,
                Format = ReleaseFormat.Dvd,
                PlatformId = i == 0 ? platformId : null,
                Source = ReleaseSource.Provider,
                CreatedAt = now,
            };
            context.Works.Add(work);
            context.Releases.Add(release);
            context.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = release.Id, Type = IdentifierType.Gtin13, Value = Barcode });
            await context.SaveChangesAsync();
            ids.Add(release.Id);
        }

        return ids;
    }

    [Fact]
    public async Task Releases_sharing_a_barcode_all_come_back_in_id_order()
    {
        var ids = await SeedReleasesSharingBarcodeAsync(5);

        var result = await LookupAsync(new CommandCounter());

        result.IsSuccess.ShouldBeTrue();
        result.Value.Code.ShouldBe(Barcode);
        result.Value.Releases.Select(r => r.Id).ShouldBe(ids.Order());
        result.Value.Releases.Select(r => r.Identifiers.Count).ShouldAllBe(count => count == 1);
        result.Value.Releases.Count(r => r.Platform is not null).ShouldBe(1);
    }

    [Fact]
    public async Task Releases_sharing_a_barcode_load_as_a_set_not_one_query_each()
    {
        await SeedReleasesSharingBarcodeAsync(5);
        var counter = new CommandCounter();

        var result = await LookupAsync(counter);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Releases.Count.ShouldBe(5);
        counter.Count.ShouldBeLessThanOrEqualTo(6); // the id lookup plus one query per table, however many share the code
    }

    private async Task<Result<CatalogLookupDto>> LookupAsync(CommandCounter counter)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContextOptions.Configure(builder, Db.ConnectionString);
        builder.AddInterceptors(counter);
        await using var context = new AppDbContext(builder.Options, new CurrentUser());
        var books = new BookLookup([], new EmptyIsbnStore(), NullLogger<BookLookup>.Instance);
        var handler = new LookupCatalog.Handler(context, [], books, new CatalogOptions(), NullLogger<LookupCatalog.Handler>.Instance);
        return await handler.HandleAsync(new LookupCatalog.Query(Barcode), CancellationToken.None);
    }

    private sealed class EmptyIsbnStore : IIsbnStore
    {
        public Task<ProviderRelease?> FindAsync(string isbn13, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task SaveAsync(string isbn13, ProviderRelease release, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Interlocked.Increment(ref Count);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
