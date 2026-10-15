using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Audit;
using CollectionMaxxing.Infrastructure.Ids;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Infrastructure.Tests.Audit;

public class AuditTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedOrigin(string? address) : IRequestOrigin
    {
        public string? Address => address;
    }

    private static AuditLog NewLog(IAppDbContext context, ICurrentUser user, string? address, FakeTimeProvider time, string? salt = "pepper") =>
        new(context, user, new FixedOrigin(address), time, new Uuid7IdGenerator(time), Options.Create(new AuditOptions { IpSalt = salt }));

    [Fact]
    public async Task An_event_stores_the_actor_the_library_and_the_time_but_only_a_hash_of_the_address()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var time = new FakeTimeProvider(Now);
        await using (var context = Db.CreateContext())
        {
            var log = NewLog(context, library.ActingAs(LibraryRole.Editor), "203.0.113.7", time);
            log.Record("item.deleted", "item", Guid.NewGuid(), library.Id, new Dictionary<string, object> { ["count"] = 3 });
            await context.SaveChangesAsync();
        }

        await using var read = Db.CreateContext();
        var audit = (await read.AuditEvents.AsNoTracking().ToListAsync()).ShouldHaveSingleItem();
        audit.ActorUserId.ShouldBe(library.EditorId);
        audit.LibraryId.ShouldBe(library.Id);
        audit.At.ShouldBe(Now);
        audit.IpHash.ShouldNotBeNull();
        audit.IpHash.ShouldNotContain("203.0.113.7");
        audit.IpHash.Length.ShouldBe(64);
        System.Text.Json.JsonDocument.Parse(audit.DetailsJson!).RootElement.GetProperty("count").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task The_same_address_hashes_the_same_and_a_different_salt_changes_it()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var time = new FakeTimeProvider(Now);
        await using (var context = Db.CreateContext())
        {
            var user = library.ActingAs(LibraryRole.Owner);
            NewLog(context, user, "198.51.100.1", time).Record("a", null, null, library.Id);
            NewLog(context, user, "198.51.100.1", time).Record("b", null, null, library.Id);
            NewLog(context, user, "198.51.100.1", time, salt: "other").Record("c", null, null, library.Id);
            NewLog(context, user, null, time).Record("d", null, null, library.Id);
            await context.SaveChangesAsync();
        }

        await using var read = Db.CreateContext();
        var byAction = await read.AuditEvents.AsNoTracking().ToDictionaryAsync(e => e.Action, e => e.IpHash);
        byAction["a"].ShouldBe(byAction["b"]);
        byAction["c"].ShouldNotBe(byAction["a"]);
        byAction["d"].ShouldBeNull();
    }

    [Fact]
    public async Task Free_text_in_details_is_refused()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await using var context = Db.CreateContext();
        var log = NewLog(context, library.ActingAs(LibraryRole.Owner), null, new FakeTimeProvider(Now));

        Should.Throw<ArgumentException>(() =>
            log.Record("item.deleted", "item", null, library.Id, new Dictionary<string, object> { ["title"] = "Secret Title" }));
    }

    [Fact]
    public async Task Pruning_removes_events_older_than_a_year_and_keeps_newer_ones()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var time = new FakeTimeProvider(Now);
        await using (var context = Db.CreateContext())
        {
            foreach (var (action, age) in new[] { ("old", TimeSpan.FromDays(365) + TimeSpan.FromSeconds(1)), ("fresh", TimeSpan.FromDays(365) - TimeSpan.FromSeconds(1)) })
            {
                var then = new FakeTimeProvider(Now - age);
                NewLog(context, library.ActingAs(LibraryRole.Owner), null, then).Record(action, null, null, library.Id);
            }

            await context.SaveChangesAsync();
        }

        await using (var context = Db.CreateContext())
        {
            await new PruneAuditEvents.Handler(context, time).HandleAsync(new PruneAuditEventsPayload(), CancellationToken.None);
        }

        await using var read = Db.CreateContext();
        (await read.AuditEvents.Select(e => e.Action).ToListAsync()).ShouldBe(["fresh"]);
    }

    [Fact]
    public async Task Anonymising_an_actor_keeps_their_events_but_drops_who_did_them()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var time = new FakeTimeProvider(Now);
        await using (var context = Db.CreateContext())
        {
            NewLog(context, library.ActingAs(LibraryRole.Editor), null, time).Record("x", null, null, library.Id);
            NewLog(context, library.ActingAs(LibraryRole.Owner), null, time).Record("y", null, null, library.Id);
            await context.SaveChangesAsync();
        }

        await using (var context = Db.CreateContext())
        {
            (await new AnonymiseAuditActor.Handler(context).HandleAsync(library.EditorId, CancellationToken.None)).ShouldBe(1);
        }

        await using var read = Db.CreateContext();
        var events = await read.AuditEvents.AsNoTracking().OrderBy(e => e.Action).ToListAsync();
        events.Select(e => e.Action).ShouldBe(["x", "y"]);
        events[0].ActorUserId.ShouldBeNull();
        events[1].ActorUserId.ShouldBe(library.OwnerId);
    }
}
