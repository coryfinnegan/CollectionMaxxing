using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Deletion;

// LOCKED acceptance tests of LIB-2c (issue #67), the purge 30 days after deleting an account. Do not edit, weaken, skip or delete:
// add new tests instead.
public class AccountPurgeTests(ApiFixture api) : DeletionTest(api)
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    [Fact]
    public async Task The_sole_owner_library_and_everything_in_it_is_purged_after_30_days_and_at_day_29_it_all_still_exists()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var libraryId = owner.LibraryId!.Value;
        var seeded = await SeedLibraryAsync(libraryId, owner.UserId);
        (await RowsOfLibraryAsync(libraryId)).Keys.ShouldBe(
            new[]
            {
                "libraries.id", "library_members.library_id", "locations.library_id", "tags.library_id", "item_tags.library_id", "items.library_id",
                "item_feature_prints.library_id", "scan_sessions.library_id", "scan_snaps.library_id", "scan_candidates.library_id",
                "match_feedback.library_id", "library_invites.library_id", "releases.owner_library_id", "assets.owner_library_id",
            },
            ignoreOrder: true,
            "the seed should put something in every table that hangs from a library");
        seeded.BlobPaths.ShouldAllBe(path => FileExists(path));

        (await DeleteAccountAsync(browser)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        App.Time.Advance(29 * Day);
        await RunDueJobsAsync();
        (await RowsOfLibraryAsync(libraryId)).ShouldNotBeEmpty("on day 29 nothing has been purged");
        (await RowsOfLibraryAsync(libraryId))["items.library_id"].ShouldBe(2);
        seeded.BlobPaths.ShouldAllBe(path => FileExists(path));

        App.Time.Advance(Day + TimeSpan.FromSeconds(1));
        await RunDueJobsAsync();

        (await RowsOfLibraryAsync(libraryId)).ShouldBeEmpty("the library and all of its dependent rows are gone");
        seeded.BlobPaths.ShouldAllBe(path => !FileExists(path), "covers and photos are removed from the disk too");
        await using var context = Db.CreateContext();
        (await context.Works.CountAsync()).ShouldBeGreaterThan(0, "the shared catalogue stays");
        (await context.Releases.IgnoreQueryFilters().CountAsync(r => r.OwnerLibraryId == null)).ShouldBeGreaterThan(0);
        (await context.Jobs.AsNoTracking().Where(j => j.Type == "account.purge").ToListAsync()).ShouldHaveSingleItem().Status.ShouldBe(JobStatus.Succeeded);
    }

    [Fact]
    public async Task The_purge_removes_the_users_sign_in_material_and_leaves_a_name_free_tombstone()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        await PairAsync(browser);
        await using (var context = Db.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                insert into idempotency_keys (user_id, key, request_hash, response_status, created_at, expires_at)
                values ({owner.UserId}, 'k', 'h', 201, {App.Time.GetUtcNow()}, {App.Time.GetUtcNow().AddDays(1)})
                """);
        }

        (await PersonalRowsAsync(owner.UserId)).Keys.ShouldContain("user_passkeys");
        (await DeleteAccountAsync(browser)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        App.Time.Advance(30 * Day + TimeSpan.FromSeconds(1));
        await RunDueJobsAsync();

        (await PersonalRowsAsync(owner.UserId)).ShouldBeEmpty();
        await using var read = Db.CreateContext();
        var user = await read.Users.AsNoTracking().SingleAsync(u => u.Id == owner.UserId);
        user.DisplayName.ShouldBe("Removed user");
        user.Email.ShouldBeNull();
        user.DeletedAt.ShouldNotBeNull();
        user.PasswordHash.ShouldBeNull();
    }

    [Fact]
    public async Task Items_a_member_added_to_a_shared_library_stay_and_say_added_by_a_removed_user()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (sam, samAccount) = await AddMemberAsync(owner, libraryId, "editor", "Sam");
        var releaseId = await CollectionMaxxing.TestSupport.TestData.CreateReleaseAsync(Db);
        var added = await sam.SendAsync(
            HttpMethod.Post,
            $"/api/v1/libraries/{libraryId}/items",
            new { releaseId, status = "owned" },
            tweak: request => request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString()));
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await BodyOfAsync(added));
        var itemId = (await ReadAsync(added)).GetProperty("id").GetGuid();

        (await DeleteAccountAsync(sam)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        App.Time.Advance(30 * Day + TimeSpan.FromSeconds(1));
        await RunDueJobsAsync();

        (await owner.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.OK); // 30 days on, the owner's old session has long expired
        var item = await ReadAsync(await owner.GetAsync($"/api/v1/libraries/{libraryId}/items/{itemId}"));
        item.GetProperty("item").GetProperty("createdByName").GetString().ShouldBe("Removed user");
        (await CountMembersAsync(libraryId)).ShouldBe(1, "Sam is no longer a member");
        (await RowsOfLibraryAsync(libraryId))["libraries.id"].ShouldBe(1, "the shared library stays");
        (await PersonalRowsAsync(samAccount.UserId)).ShouldBeEmpty();
        (await owner.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Audit_entries_for_the_user_survive_but_carry_no_identity_after_the_purge()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var libraryId = owner.LibraryId!.Value;
        await using (var context = Db.CreateContext())
        {
            foreach (var action in new[] { "item.deleted", "item.restored", "items.bulk_deleted" })
            {
                context.AuditEvents.Add(new AuditEventSeed(owner.UserId, libraryId, action, App.Time.GetUtcNow()).ToEvent());
            }

            await context.SaveChangesAsync();
        }

        (await DeleteAccountAsync(browser)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var before = Db.CreateContext())
        {
            (await before.AuditEvents.CountAsync(e => e.ActorUserId == owner.UserId)).ShouldBeGreaterThanOrEqualTo(4, "the three seeded and the deletion itself");
        }

        var totalBefore = await CountAuditAsync();
        App.Time.Advance(30 * Day + TimeSpan.FromSeconds(1));
        await RunDueJobsAsync();

        await using var read = Db.CreateContext();
        var events = await read.AuditEvents.AsNoTracking().ToListAsync();
        events.Count.ShouldBeGreaterThanOrEqualTo(totalBefore, "the events survive (and the purge adds its own)");
        events.ShouldAllBe(e => e.ActorUserId == null, "nobody is named any more");
        events.ShouldAllBe(e => e.IpHash == null, "not even a hash of an address");
        events.ShouldAllBe(e => e.EntityId != owner.UserId, "nor is the user the subject of an event");
        events.Select(e => e.Action).ShouldContain("account.deleted");
        events.Select(e => e.Action).ShouldContain("item.deleted");
        events.Where(e => e.DetailsJson != null).ShouldAllBe(e => !e.DetailsJson!.Contains(owner.UserId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Nothing_of_the_user_is_left_in_the_logs_the_provider_cache_or_the_job_queue()
    {
        var (browser, owner) = await SetUpNamedOwnerAsync("Zanzibar Quill");
        var libraryId = owner.LibraryId!.Value;
        await SeedLibraryAsync(libraryId, owner.UserId);
        await PairAsync(browser);
        await using (var context = Db.CreateContext())
        {
            context.ProviderCache.Add(new ProviderCacheEntry
            {
                Provider = "tmdb",
                Key = "search:the matrix",
                PayloadJson = """{"results":[{"title":"The Matrix"}]}""",
                FetchedAt = App.Time.GetUtcNow(),
                ExpiresAt = App.Time.GetUtcNow().AddDays(30),
            });
            await context.SaveChangesAsync();
        }

        (await DeleteAccountAsync(browser)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        App.Time.Advance(30 * Day + TimeSpan.FromSeconds(1));
        await RunDueJobsAsync();
        (await RowsOfLibraryAsync(libraryId)).ShouldBeEmpty();

        var secrets = new List<string>
        {
            "Zanzibar Quill",
            owner.UserId.ToString(),
            owner.UserId.ToString("N"),
            owner.UserId.ToString("N").ToUpperInvariant(),
            browser.Authenticator.CredentialIdText,
        };
        secrets.AddRange(browser.SetCookies.Select(c => c.Value.Value!).Where(v => v.Length > 8));
        App.Logs.Lines.Count.ShouldBeGreaterThan(0, "the capture should see the app's log lines");
        foreach (var secret in secrets)
        {
            App.Logs.Lines.Where(line => line.Contains(secret, StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty($"a log line contains '{secret[..6]}…'");
        }

        await using var read = Db.CreateContext();
        var cache = await read.ProviderCache.AsNoTracking().ToListAsync();
        cache.ShouldNotBeEmpty();
        foreach (var secret in secrets)
        {
            cache.ShouldAllBe(e => !e.Key.Contains(secret, StringComparison.OrdinalIgnoreCase) && !e.PayloadJson.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        // The finished job no longer says whose account it was.
        var jobs = await read.Jobs.AsNoTracking().ToListAsync();
        jobs.ShouldNotBeEmpty();
        jobs.ShouldAllBe(j => !j.PayloadJson.Contains(owner.UserId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<int> CountAuditAsync()
    {
        await using var context = Db.CreateContext();
        return await context.AuditEvents.CountAsync();
    }

    private sealed record AuditEventSeed(Guid ActorId, Guid LibraryId, string Action, DateTimeOffset At)
    {
        public AuditEvent ToEvent() => new()
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = ActorId,
            LibraryId = LibraryId,
            Action = Action,
            EntityType = "user",
            EntityId = ActorId,
            At = At,
            IpHash = "00ff00ff",
            DetailsJson = $$"""{"by":"{{ActorId}}"}""",
        };
    }
}
