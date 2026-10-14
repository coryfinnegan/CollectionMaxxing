using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Infrastructure.Identity.Pairing;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Pairing;

// LOCKED acceptance tests of ID-2b (issue #63): spent and expired pairing rows are purged after the retention window; open ones are not.
// Do not edit, weaken, skip or delete: add new tests instead.
public class PairingCleanupTests(ApiFixture api) : HardeningTest(api)
{
    [Fact]
    public async Task Spent_and_expired_pairings_older_than_the_retention_window_are_removed()
    {
        var user = await TestData.CreateUserAsync(Db);
        var now = App.Time.GetUtcNow();
        var spentOld = await AddAsync(user, created: now - TimeSpan.FromDays(3), used: now - TimeSpan.FromDays(3));
        var expiredOld = await AddAsync(user, created: now - TimeSpan.FromDays(3), used: null);

        var removed = await PurgeAsync();

        removed.ShouldBe(2);
        (await IdsAsync()).ShouldNotContain(spentOld);
        (await IdsAsync()).ShouldNotContain(expiredOld);
    }

    [Fact]
    public async Task Open_and_recently_finished_pairings_are_left_alone()
    {
        var user = await TestData.CreateUserAsync(Db);
        var now = App.Time.GetUtcNow();
        var open = await AddAsync(user, created: now - TimeSpan.FromSeconds(30), used: null);
        var spentRecently = await AddAsync(user, created: now - TimeSpan.FromHours(2), used: now - TimeSpan.FromHours(2));
        var expiredRecently = await AddAsync(user, created: now - TimeSpan.FromHours(5), used: null);

        var removed = await PurgeAsync();

        removed.ShouldBe(0);
        (await IdsAsync()).ShouldBe([open, spentRecently, expiredRecently], ignoreOrder: true);
    }

    [Fact]
    public async Task An_open_pairing_is_not_removed_however_old_the_clock_says_the_others_are()
    {
        var user = await TestData.CreateUserAsync(Db);
        var now = App.Time.GetUtcNow();
        var open = await AddAsync(user, created: now - TimeSpan.FromSeconds(10), used: null);
        var stale = await AddAsync(user, created: now - TimeSpan.FromDays(9), used: now - TimeSpan.FromDays(9));

        await PurgeAsync();

        (await IdsAsync()).ShouldBe([open]);
        stale.ShouldNotBe(open);
    }

    [Fact]
    public async Task A_purged_phone_keeps_its_name_in_the_devices_list()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        (await RedeemTokenAsync(From("10.1.1.1"), offer.Token, "Cory's iPhone 17")).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        App.Time.Advance(TimeSpan.FromDays(3));

        // Advancing the clock also fires the background sweep, which may get to the row first, so only the outcome is asserted.
        await PurgeAsync();
        (await IdsAsync()).ShouldBeEmpty();

        (await browser.LoginAsync()).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var sessions = await browser.GetAsync("/api/v1/me/sessions");
        var device = (await ReadJsonAsync(sessions)).GetProperty("items").EnumerateArray().Single();
        device.GetProperty("deviceName").GetString().ShouldBe("Cory's iPhone 17");
    }

    [Fact]
    public async Task The_cleanup_runs_by_itself_as_time_passes()
    {
        _ = App.Services; // start the app, and with it the cleanup
        var user = await TestData.CreateUserAsync(Db);
        var now = App.Time.GetUtcNow();
        await AddAsync(user, created: now - TimeSpan.FromDays(3), used: now - TimeSpan.FromDays(3));
        var open = await AddAsync(user, created: now, used: null);

        App.Time.Advance(TimeSpan.FromHours(1)); // one sweep interval

        await EventuallyAsync(async () => (await IdsAsync()).Count == 1);
        (await IdsAsync()).ShouldBe([open]);
    }

    private async Task<int> PurgeAsync()
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IDevicePairingCleanup>().PurgeAsync(CancellationToken.None);
    }

    private async Task<List<Guid>> IdsAsync()
    {
        await using var context = Db.CreateContext();
        return await context.DevicePairings.AsNoTracking().Select(p => p.Id).ToListAsync();
    }

    private async Task<Guid> AddAsync(Guid userId, DateTimeOffset created, DateTimeOffset? used)
    {
        var id = Guid.CreateVersion7();
        await using var context = Db.CreateContext();
        context.DevicePairings.Add(new DevicePairing
        {
            Id = id,
            UserId = userId,
            TokenHash = Sha256Hex("token-" + id),
            CodeHash = Sha256Hex("code-" + id),
            CreatedAt = created,
            ExpiresAt = created + TimeSpan.FromMinutes(2),
            UsedAt = used,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The cleanup did not run.");
    }
}
