using System.Collections.Concurrent;
using System.Data.Common;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Catalog;
using CollectionMaxxing.Infrastructure.Persistence;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Infrastructure.Tests.Catalog;

/// <summary>
/// A shelf photo looks up dozens of titles in the local catalogue. Looking them up together must give each title exactly the hits
/// looking it up alone gives, in the same order, in a handful of queries instead of one each.
/// </summary>
public class LocalCatalogSearchManyTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset Created = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Titles =
    [
        "Star Fox 64", "Star Fox Adventures", "Star Wars", "Starship Troopers", "Mario Kart 64", "Mario Kart Wii", "Mario Party",
        "Banjo-Kazooie", "Banjo-Tooie", "The Matrix", "The Matrix Reloaded", "The Matrix Revolutions", "Matrix", "Dune", "Dune Messiah",
        "Children of Dune", "Neuromancer", "Snow Crash", "Hyperion", "The Fall of Hyperion", "Foundation", "Foundation and Empire",
        "Second Foundation", "Solaris", "Kindred",
    ];

    private async Task<(Guid Platform, Guid OtherLibrary)> SeedAsync()
    {
        var other = (await TestData.CreateLibraryAsync(Db)).Id;
        var platform = Guid.CreateVersion7();
        await using var context = Db.CreateContext();
        context.Platforms.Add(new Platform { Id = platform, Name = "Nintendo 64", ShortName = "N64" });
        for (var i = 0; i < Titles.Length; i++)
        {
            var work = new Work
            {
                Id = Guid.CreateVersion7(),
                Kind = i < 9 ? WorkKind.Game : i < 13 ? WorkKind.Film : WorkKind.Book,
                Title = Titles[i],
                SortTitle = Titles[i],
                NormalizedTitle = TitleNormalizer.Normalize(Titles[i]),
                OriginalYear = 1990 + (i % 7),
                CreatedAt = Created,
            };
            context.Works.Add(work);
            foreach (var format in work.Kind == WorkKind.Film ? new[] { ReleaseFormat.Vhs, ReleaseFormat.Dvd } : [ReleaseFormat.Other])
            {
                context.Releases.Add(new Release
                {
                    Id = Guid.CreateVersion7(),
                    WorkId = work.Id,
                    Format = format,
                    PlatformId = work.Kind == WorkKind.Game && i % 2 == 0 ? platform : null,
                    Source = ReleaseSource.Provider,
                    CreatedAt = Created,
                });
            }
        }

        // A private release of another library's: nobody else may find it.
        var hiddenWork = new Work
        {
            Id = Guid.CreateVersion7(),
            Kind = WorkKind.Book,
            Title = "Star Fox Secret",
            SortTitle = "Star Fox Secret",
            NormalizedTitle = TitleNormalizer.Normalize("Star Fox Secret"),
            CreatedAt = Created,
        };
        context.Works.Add(hiddenWork);
        context.Releases.Add(new Release
        {
            Id = Guid.CreateVersion7(),
            WorkId = hiddenWork.Id,
            Format = ReleaseFormat.Paperback,
            Source = ReleaseSource.User,
            OwnerLibraryId = other,
            CreatedAt = Created,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (platform, other);
    }

    private ServiceProvider Services(CommandLog? log = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddDbContext<AppDbContext>(options =>
        {
            AppDbContextOptions.Configure(options, Db.ConnectionString);
            if (log is not null)
            {
                options.AddInterceptors(log);
            }
        });
        services.AddCatalogStorage();
        return services.BuildServiceProvider();
    }

    private static string Describe(IReadOnlyList<LocalHit> hits) =>
        string.Join(" | ", hits.Select(h => $"{h.Work.Title}/{h.Release.Format}/{h.Release.Id}/{h.Work.Id}"));

    [Fact]
    public async Task Each_title_gets_the_hits_looking_it_up_alone_gives_in_the_same_order()
    {
        var (platform, _) = await SeedAsync();
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var search = scope.ServiceProvider.GetRequiredService<ILocalCatalogSearch>();
        var normalized = (string title) => TitleNormalizer.Normalize(title);
        LocalSearch[] searches =
        [
            new(normalized("Star Fox 64"), null, null, null, 50),
            new(normalized("star fox"), null, null, null, 50),
            new(normalized("Matrix"), null, null, null, 50),
            new(normalized("Matrix"), null, null, null, 2),
            new(normalized("Matrix"), WorkKind.Film, null, null, 50),
            new(normalized("Matrix"), WorkKind.Book, null, null, 50),
            new(normalized("Mario"), WorkKind.Game, [platform], null, 50),
            new(normalized("Foundation"), null, null, 1992, 50),
            new(normalized("Dune"), null, null, null, 50),
            new(normalized("Xyzzy Plugh Frobozz"), null, null, null, 50),
            new(normalized("Banjo"), null, null, null, 50),
            new(normalized("Hyperion"), WorkKind.Book, null, null, 50),
            new(normalized("Star Fox 64"), null, null, null, 50),
            new(normalized("Secret"), null, null, null, 50),
        ];

        var together = await search.FindManyAsync(searches, TestContext.Current.CancellationToken);

        together.Count.ShouldBe(searches.Length);
        for (var i = 0; i < searches.Length; i++)
        {
            var alone = await search.FindAsync(searches[i], TestContext.Current.CancellationToken);
            Describe(together[i]).ShouldBe(Describe(alone), $"search {i}: {searches[i]}");
        }

        together[0].ShouldNotBeEmpty();
        together[3].Count.ShouldBe(2);
        together[9].ShouldBeEmpty();
        together[13].ShouldBeEmpty(); // the only title with "secret" is another library's private release
    }

    [Fact]
    public async Task Another_librarys_private_release_is_found_by_that_library_only_and_in_a_batch_as_alone()
    {
        var (_, other) = await SeedAsync();
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        ((CurrentUser)user).Initialize(Guid.CreateVersion7(), [new Membership(other, LibraryRole.Editor)]);
        var search = scope.ServiceProvider.GetRequiredService<ILocalCatalogSearch>();
        var secret = new LocalSearch(TitleNormalizer.Normalize("Star Fox Secret"), null, null, null, 50);

        var together = await search.FindManyAsync([secret, secret], TestContext.Current.CancellationToken);

        together[0].Select(h => h.Work.Title).ShouldContain("Star Fox Secret");
        Describe(together[1]).ShouldBe(Describe(await search.FindAsync(secret, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Many_titles_are_looked_up_in_a_few_queries_not_one_each()
    {
        await SeedAsync();
        var log = new CommandLog();
        await using var services = Services(log);
        await using var scope = services.CreateAsyncScope();
        var search = scope.ServiceProvider.GetRequiredService<ILocalCatalogSearch>();
        var searches = Titles.Concat(Titles).Select(t => new LocalSearch(TitleNormalizer.Normalize(t), null, null, null, 50)).ToList();

        var together = await search.FindManyAsync(searches, TestContext.Current.CancellationToken);

        together.Count.ShouldBe(searches.Count);
        log.Count.ShouldBeLessThanOrEqualTo(8, $"{searches.Count} titles took {log.Count} queries");
    }

    [Fact]
    public async Task Looking_up_nothing_runs_no_query()
    {
        var log = new CommandLog();
        await using var services = Services(log);
        await using var scope = services.CreateAsyncScope();

        var together = await scope.ServiceProvider.GetRequiredService<ILocalCatalogSearch>().FindManyAsync([], TestContext.Current.CancellationToken);

        together.ShouldBeEmpty();
        log.Count.ShouldBe(0);
    }

    private sealed class CommandLog : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public int Count => _commands.Count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
