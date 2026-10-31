using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Persistence;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// A shelf photo reads 30 spines at once. The answer for each read is looked up in the database, and a lookup per read is a round
// trip per read: 30 reads cost hundreds of serial queries. These tests pin both halves of the fix: what a realistic shelf answers
// (every candidate, decision, score, alternative and ownership flag, byte for byte) and how many times the database is asked.
public class IdentifyRoundTripTests : IdentifyTest
{
    /// <summary>
    /// The most database round trips one identify of <see cref="ShelfSize"/> reads may take. A query for each read would add
    /// <see cref="ShelfSize"/> to the count, so this sits well under that.
    /// </summary>
    private const int RoundTripCeiling = 55;

    private const int ShelfSize = 30;

    private static readonly Guid Platform = Fixed(1);

    private static readonly Guid SnapId = Fixed(9000);

    private readonly RoundTripRecorder _recorder = new();

    public IdentifyRoundTripTests(ApiFixture api)
        : base(api)
    {
        ConfigureServices = services => services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(_recorder));
    }

    [Fact]
    public async Task A_shelf_of_thirty_reads_gets_exactly_the_answer_it_always_got()
    {
        var (client, sessionId) = await ArrangeShelfAsync();

        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(ShelfBody()));

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, string.Join(" || ", Logs.Lines.Where(l => l.Contains("Exception", StringComparison.Ordinal))));
        var answer = Pin.Normalized(await response.Content.ReadAsStringAsync());
        Pin.Check("shelf-of-thirty.json", answer);
    }

    [Fact]
    public async Task A_shelf_of_thirty_reads_is_answered_in_a_handful_of_round_trips_not_a_few_per_read()
    {
        var (client, sessionId) = await ArrangeShelfAsync();

        _recorder.Clear();
        var clock = Stopwatch.StartNew();
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(ShelfBody()));
        clock.Stop();
        var trips = _recorder.Snapshot();

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"identify of {ShelfSize} reads: {trips.Count} round trips in {clock.ElapsedMilliseconds} ms{Environment.NewLine}{RoundTripRecorder.Report(trips)}");
        trips.Count.ShouldBeLessThanOrEqualTo(RoundTripCeiling, RoundTripRecorder.Report(trips));
    }

    private async Task<(HttpClient Client, Guid SessionId)> ArrangeShelfAsync()
    {
        var library = await CreateLibraryAsync();
        await SeedCatalogueAsync(library);
        Games.Results.Add(new ProviderRelease
        {
            Provider = "igdb", ExternalId = "g-zorblax", Title = "Zorblax Quest IV", Kind = WorkKind.Game, Format = ReleaseFormat.GameCartridge,
            Platform = "Nintendo 64", Year = 1998,
        });
        Games.Results.Add(new ProviderRelease
        {
            Provider = "igdb", ExternalId = "g-quantum", Title = "Quantum Racer 64", Kind = WorkKind.Game, Format = ReleaseFormat.GameCartridge,
            Platform = "Nintendo 64", Year = 1999,
        });
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(client, library.Id, new { autoCommitTiers = new[] { "T0", "T1" } });
        return (client, sessionId);
    }

    private static Dictionary<string, object?> ShelfBody()
    {
        var reads = Shelf();
        for (var i = 0; i < reads.Count; i++)
        {
            reads[i]["localId"] = $"read-{i:D2}"; // the phone's own id for a read: fixed here so two runs compare equal
        }

        var body = Body(SnapId, reads);
        body["shotKind"] = "shelf_segment";
        return body;
    }

    private static Dictionary<string, object?> Game(string title, int year) =>
        Read(title: title, kind: "game", format: "game_cartridge", platform: "Nintendo 64", year: year);

    private static Dictionary<string, object?> Book(string title, string creator) => Read(title: title, kind: "book", creator: creator);

    private static Dictionary<string, object?> Film(string title) => Read(title: title, kind: "film", format: "dvd");

    /// <summary>
    /// 30 reads of a shelf: games, books and films the catalogue holds (some the library owns, one twice), a title with two formats,
    /// two reads the phone already matched by feature print, a text-only read, two titles only a provider knows, three nobody knows,
    /// and the first read once more.
    /// </summary>
    private static List<Dictionary<string, object?>> Shelf() =>
    [
        Game("Star Fox 64", 1997),
        Game("Mario Kart 64", 1996),
        Game("Banjo-Kazooie", 1998),
        Game("GoldenEye 007", 1997),
        Game("Paper Mario", 2000),
        Game("Pilotwings 64", 1996),
        Game("Wave Race 64", 1996),
        Game("Blast Corps", 1997),
        Book("Dune", "Frank Herbert"),
        Book("Neuromancer", "William Gibson"),
        Book("Hyperion", "Dan Simmons"),
        Book("Snow Crash", "Neal Stephenson"),
        Book("Foundation", "Isaac Asimov"),
        Book("Solaris", "Stanislaw Lem"),
        Book("Kindred", "Octavia Butler"),
        Book("Gateway", "Frederik Pohl"),
        Film("Jurassic Park"),
        Film("Casablanca"),
        Film("Alien"),
        Read(title: "The Matrix", kind: "film"),
        Read(title: "The Matrix", kind: "film", format: "vhs"),
        Read(title: "Ubik", kind: "book", localMatchReleaseId: ReleaseOf(BookRows, "Ubik")),
        Read(title: "Rendezvous with Rama", kind: "book", localMatchReleaseId: ReleaseOf(BookRows, "Rendezvous with Rama")),
        Read(title: null, text: "HEAT", kind: "film"),
        Read(title: "Zorblax Quest IV", kind: "game", platform: "Nintendo 64", year: 1998),
        Read(title: "Quantum Racer 64", kind: "game", platform: "Nintendo 64", year: 1999),
        Read(title: "Xyzzy Plugh Frobozz"),
        Read(title: "Qwerty Uiop Asdfgh"),
        Read(title: "Bleep Bloop Blorptastic"),
        Game("Star Fox 64", 1997),
    ];

    private sealed record Row(string Title, WorkKind Kind, ReleaseFormat Format, int Year, string? Creator = null, bool Owned = false, int Copies = 1);

    private static readonly Row[] GameRows =
    [
        new("Star Fox 64", WorkKind.Game, ReleaseFormat.GameCartridge, 1997, Owned: true),
        new("Mario Kart 64", WorkKind.Game, ReleaseFormat.GameCartridge, 1996),
        new("Banjo-Kazooie", WorkKind.Game, ReleaseFormat.GameCartridge, 1998),
        new("GoldenEye 007", WorkKind.Game, ReleaseFormat.GameCartridge, 1997, Owned: true),
        new("Paper Mario", WorkKind.Game, ReleaseFormat.GameCartridge, 2000),
        new("Pilotwings 64", WorkKind.Game, ReleaseFormat.GameCartridge, 1996),
        new("Wave Race 64", WorkKind.Game, ReleaseFormat.GameCartridge, 1996),
        new("Blast Corps", WorkKind.Game, ReleaseFormat.GameCartridge, 1997),
    ];

    private static readonly Row[] BookRows =
    [
        new("Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965, "Frank Herbert", Owned: true, Copies: 2),
        new("Neuromancer", WorkKind.Book, ReleaseFormat.Paperback, 1984, "William Gibson"),
        new("Hyperion", WorkKind.Book, ReleaseFormat.Paperback, 1989, "Dan Simmons"),
        new("Snow Crash", WorkKind.Book, ReleaseFormat.Paperback, 1992, "Neal Stephenson", Owned: true),
        new("Foundation", WorkKind.Book, ReleaseFormat.Paperback, 1951, "Isaac Asimov"),
        new("Solaris", WorkKind.Book, ReleaseFormat.Paperback, 1961, "Stanislaw Lem"),
        new("Kindred", WorkKind.Book, ReleaseFormat.Paperback, 1979, "Octavia Butler"),
        new("Gateway", WorkKind.Book, ReleaseFormat.Paperback, 1977, "Frederik Pohl"),
        new("Ubik", WorkKind.Book, ReleaseFormat.Paperback, 1969, "Philip K. Dick"),
        new("Rendezvous with Rama", WorkKind.Book, ReleaseFormat.Paperback, 1973, "Arthur C. Clarke"),
    ];

    private static readonly Row[] FilmRows =
    [
        new("Jurassic Park", WorkKind.Film, ReleaseFormat.Dvd, 1993, Owned: true),
        new("Casablanca", WorkKind.Film, ReleaseFormat.Dvd, 1942),
        new("Alien", WorkKind.Film, ReleaseFormat.Dvd, 1979),
        new("The Matrix", WorkKind.Film, ReleaseFormat.Dvd, 1999),
        new("The Matrix", WorkKind.Film, ReleaseFormat.Vhs, 1999),
        new("Heat", WorkKind.Film, ReleaseFormat.Dvd, 1995),
    ];

    private static Guid Fixed(int n) => new($"00000000-0000-7000-8000-{n:D12}");

    /// <summary>Releases get ids from 2000 up, in the order the rows are seeded (games, then books, then films).</summary>
    private static Guid ReleaseOf(Row[] rows, string title)
    {
        var all = Seeded();
        return Fixed(2000 + all.FindIndex(r => r.Title == title && rows.Contains(r)));
    }

    private static List<Row> Seeded() => [.. GameRows, .. BookRows, .. FilmRows];

    private async Task SeedCatalogueAsync(TestLibrary library)
    {
        await using var context = Db.CreateContext();
        context.Platforms.Add(new Platform { Id = Platform, Name = "Nintendo 64", ShortName = "N64" });
        var created = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var rows = Seeded();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var work = new Work
            {
                Id = Fixed(1000 + i),
                Kind = row.Kind,
                Title = row.Title,
                SortTitle = row.Title.ToLowerInvariant(),
                NormalizedTitle = TitleNormalizer.Normalize(row.Title),
                OriginalYear = row.Year,
                CreatorsJson = JsonSerializer.Serialize(row.Creator is null ? Array.Empty<string>() : [row.Creator]),
                CreatedAt = created,
            };
            var release = new Release
            {
                Id = Fixed(2000 + i),
                WorkId = work.Id,
                Format = row.Format,
                PlatformId = row.Kind == WorkKind.Game ? Platform : null,
                Source = ReleaseSource.Provider,
                CreatedAt = created,
            };
            context.Works.Add(work);
            context.Releases.Add(release);
            for (var copy = 0; row.Owned && copy < row.Copies; copy++)
            {
                context.Items.Add(new Item
                {
                    Id = Fixed(3000 + (i * 10) + copy),
                    LibraryId = library.Id,
                    ReleaseId = release.Id,
                    Status = ItemStatus.Owned,
                    CoverChoice = CoverChoice.Release,
                    AddedVia = AddedVia.Manual,
                    CreatedAt = created.AddMinutes((i * 10) + copy),
                    CreatedBy = library.OwnerId,
                    UpdatedAt = created,
                });
            }
        }

        await context.SaveChangesAsync();
    }

    /// <summary>Every database command and transaction step the app runs, so a test can count them and see which statements repeat.</summary>
    private sealed class RoundTripRecorder : DbCommandInterceptor, IDbTransactionInterceptor
    {
        private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex Guids = new(@"'[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}'", RegexOptions.Compiled);

        private readonly ConcurrentQueue<string> _trips = new();

        public void Clear() => _trips.Clear();

        public List<string> Snapshot() => [.. _trips];

        /// <summary>The distinct statements, most repeated first, with how often each ran.</summary>
        public static string Report(IReadOnlyList<string> trips)
        {
            var text = new StringBuilder();
            foreach (var group in trips.GroupBy(t => t).OrderByDescending(g => g.Count()).Take(40))
            {
                text.Append(CultureInfo.InvariantCulture, $"{group.Count(),4} x {Trim(group.Key)}{Environment.NewLine}");
            }

            return text.ToString();
        }

        private static string Trim(string statement) => statement.Length > 330 ? statement[..110] + " ... " + statement[^200..] : statement;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Note(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Note(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Note(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Note(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Note(command);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Note(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            _trips.Enqueue("BEGIN");
            return ValueTask.FromResult(result);
        }

        public InterceptionResult<DbTransaction> TransactionStarting(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            _trips.Enqueue("BEGIN");
            return result;
        }

        public ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            _trips.Enqueue("COMMIT");
            return ValueTask.FromResult(result);
        }

        public InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
        {
            _trips.Enqueue("COMMIT");
            return result;
        }

        private void Note(DbCommand command) =>
            _trips.Enqueue(Guids.Replace(Spaces.Replace(command.CommandText, " ").Trim(), "'<guid>'"));
    }

    /// <summary>A response kept in a file next to the tests, compared with ids and times made the same from run to run.</summary>
    private static class Pin
    {
        private static readonly Regex GuidText = new(@"^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", RegexOptions.Compiled);

        /// <summary>
        /// The body with every id replaced by its number in order of appearance, every timestamp by a fixed one and the database's change counter by a mark, so two runs
        /// of the same request compare equal and a changed answer does not.
        /// </summary>
        public static string Normalized(string body)
        {
            using var document = JsonDocument.Parse(body);
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                Write(writer, document.RootElement, seen);
            }

            return Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n") + "\n";
        }

        public static void Check(string name, string actual)
        {
            var path = RepositoryFiles.Find("tests", "CollectionMaxxing.Api.Tests", "Scanning", "Identify", "Pinned", name);
            if (Environment.GetEnvironmentVariable("CM_PIN_WRITE") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, actual);
            }

            File.Exists(path).ShouldBeTrue($"{path} is missing; run once with CM_PIN_WRITE=1 against the code whose answer is to be kept.");
            var expected = File.ReadAllText(path).ReplaceLineEndings("\n");
            if (actual != expected)
            {
                var actualPath = Path.Combine(Path.GetTempPath(), name + ".actual");
                File.WriteAllText(actualPath, actual);
                throw new ShouldAssertException($"The answer differs from {path}; it was written to {actualPath} to compare.");
            }
        }

        private static void Write(Utf8JsonWriter writer, JsonElement element, Dictionary<string, int> seen)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        if (property.NameEquals("changeSeq"))
                        {
                            writer.WriteStringValue("<change seq>"); // the database's running counter: it differs from run to run like an id
                            continue;
                        }

                        Write(writer, property.Value, seen);
                    }

                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        Write(writer, item, seen);
                    }

                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    var text = element.GetString()!;
                    if (GuidText.IsMatch(text))
                    {
                        if (!seen.TryGetValue(text, out var number))
                        {
                            seen[text] = number = seen.Count + 1;
                        }

                        writer.WriteStringValue($"<id {number}>");
                    }
                    else if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) && text.Contains('T', StringComparison.Ordinal) && text.Contains(':', StringComparison.Ordinal))
                    {
                        writer.WriteStringValue("<time>");
                    }
                    else
                    {
                        writer.WriteStringValue(text);
                    }

                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
