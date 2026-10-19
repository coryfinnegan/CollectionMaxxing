using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Invites;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Deletion;

/// <summary>
/// Base class for the account-deletion tests (LIB-2c): real passkey sign-in, a clock the test controls, a covers folder in a
/// temporary directory, and the pieces of deleting an account: the fresh passkey assertion, a library full of data, the purge job.
/// </summary>
public abstract class DeletionTest(ApiFixture api) : InviteTest(api)
{
    protected static readonly DateTimeOffset SeedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    protected string CoversRoot { get; } = Path.Combine(Path.GetTempPath(), "cm-covers-" + Guid.NewGuid().ToString("N"));

    protected override Dictionary<string, string?>? MoreSettings => new() { ["Catalog:CoversRoot"] = CoversRoot };

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
        if (Directory.Exists(CoversRoot))
        {
            Directory.Delete(CoversRoot, recursive: true);
        }
    }

    /// <summary>Like <c>SetUpOwnerAsync</c>, with a name to look for in logs and tables.</summary>
    protected async Task<(WebClient Browser, WebClient.SignedIn Owner)> SetUpNamedOwnerAsync(string displayName)
    {
        var browser = App.NewBrowser();
        var owner = await browser.SetUpOwnerAsync(await App.IssueCodeAsync(CollectionMaxxing.Domain.Accounts.SetupCodePurpose.Setup), displayName);
        return (browser, owner);
    }

    // ---- deleting ------------------------------------------------------------------------------------------------------

    /// <summary>What the web app does just before deleting: asks for passkey options and signs them. The result is the credential JSON.</summary>
    protected static async Task<JsonElement> AssertAsync(WebClient browser, SoftwareAuthenticator? authenticator = null)
    {
        var optionsResponse = await browser.PostAsync("/account/passkey/request-options");
        optionsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var options = (await optionsResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("options");
        return JsonDocument.Parse((authenticator ?? browser.Authenticator).Sign(options.GetRawText())).RootElement.Clone();
    }

    protected static Task<HttpResponseMessage> DeleteWithAsync(WebClient browser, JsonElement credential) =>
        browser.SendAsync(HttpMethod.Delete, "/api/v1/me", new { credential });

    /// <summary>A passkey assertion made for this request, then the delete.</summary>
    protected static async Task<HttpResponseMessage> DeleteAccountAsync(WebClient browser) =>
        await DeleteWithAsync(browser, await AssertAsync(browser));

    protected async Task<(DateTimeOffset? DeletedAt, string DisplayName)> UserRowAsync(Guid userId)
    {
        await using var context = Db.CreateContext();
        var user = await context.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        return (user.DeletedAt, user.DisplayName);
    }

    protected async Task<List<Job>> PurgeJobsAsync()
    {
        await using var context = Db.CreateContext();
        return await context.Jobs.AsNoTracking().Where(j => j.Type == "account.purge").OrderBy(j => j.CreatedAt).ToListAsync();
    }

    // ---- the purge -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Lets the app's own background worker catch up with a clock that just jumped: runs due jobs until none is left, then waits until no job is
    /// running and none is due. "Nothing is running" alone is not settled: the worker may have claimed the sweep, and between the sweep finishing
    /// and the worker claiming the purge it queued, the purge is due but not running. So look again, and run what is due, until the table is quiet.
    /// </summary>
    protected async Task RunDueJobsAsync()
    {
        var runner = App.Services.GetRequiredService<JobRunner>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            while (await runner.RunNextAsync("test-" + Guid.NewGuid().ToString("N"), CancellationToken.None))
            {
            }

            var now = App.Time.GetUtcNow();
            await using var context = Db.CreateContext();
            var busy = await context.Jobs.AnyAsync(j => j.Status == JobStatus.Running || (j.Status == JobStatus.Pending && j.RunAfter <= now));
            if (!busy || DateTime.UtcNow >= deadline)
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    // ---- a library full of data ----------------------------------------------------------------------------------------

    /// <summary>The ids of what <see cref="SeedLibraryAsync"/> put in a library.</summary>
    protected sealed record Seeded(
        Guid LibraryId,
        Guid ItemId,
        Guid CoverAssetId,
        Guid PhotoAssetId,
        Guid OwnedReleaseId,
        Guid CandidateId,
        IReadOnlyList<string> BlobPaths);

    /// <summary>
    /// Fills a library with one of everything that hangs from it: a location tree, tags, items (one with the user's own cover), a feature
    /// print, a scan session with a snap and a candidate, match feedback, a release the library made, an invite, and two image files.
    /// </summary>
    protected async Task<Seeded> SeedLibraryAsync(Guid libraryId, Guid userId)
    {
        var parentLocation = Guid.CreateVersion7();
        var childLocation = Guid.CreateVersion7();
        await using (var context = Db.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                insert into locations (id, library_id, parent_id, name, sort_order) values ({parentLocation}, {libraryId}, null, 'Shelf', 0);
                insert into locations (id, library_id, parent_id, name, sort_order) values ({childLocation}, {libraryId}, {parentLocation}, 'Box', 0);
                """);
        }

        var cover = await SeedAssetAsync(libraryId, AssetKind.Cover);
        var photo = await SeedAssetAsync(libraryId, AssetKind.UserPhoto);
        var snapPhoto = await SeedAssetAsync(libraryId, AssetKind.ScanPhoto);
        var releaseId = await TestData_CreateReleaseAsync(libraryId);
        var sharedRelease = await TestData_CreateReleaseAsync(null);

        var itemId = Guid.CreateVersion7();
        var otherItemId = Guid.CreateVersion7();
        var tagId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        var snapId = Guid.CreateVersion7();
        var candidateId = Guid.CreateVersion7();
        await using (var context = Db.CreateContext())
        {
            context.Tags.Add(new Tag { Id = tagId, LibraryId = libraryId, Name = "Horror" });
            foreach (var (id, location, release, customCover) in new[] { (itemId, childLocation, releaseId, photo.Id), (otherItemId, parentLocation, sharedRelease, (Guid?)null) })
            {
                context.Items.Add(new Item
                {
                    Id = id,
                    LibraryId = libraryId,
                    ReleaseId = release,
                    LocationId = location,
                    Status = ItemStatus.Owned,
                    CoverChoice = customCover is null ? CoverChoice.Release : CoverChoice.MyPhoto,
                    CustomCoverAssetId = customCover,
                    AddedVia = AddedVia.Manual,
                    CreatedAt = SeedTime,
                    CreatedBy = userId,
                    UpdatedAt = SeedTime,
                });
            }

            context.ScanSessions.Add(new ScanSession { Id = sessionId, LibraryId = libraryId, UserId = userId, Status = ScanSessionStatus.Open, CreatedAt = SeedTime });
            await context.SaveChangesAsync();

            context.ItemTags.Add(new ItemTag { LibraryId = libraryId, ItemId = itemId, TagId = tagId });
            context.ItemFeaturePrints.Add(new ItemFeaturePrint { Id = Guid.CreateVersion7(), LibraryId = libraryId, ItemId = itemId, ReleaseId = releaseId, Revision = 1, Data = [1, 2, 3], CreatedAt = SeedTime });
            context.ScanSnaps.Add(new ScanSnap { Id = snapId, LibraryId = libraryId, SessionId = sessionId, Trigger = SnapTrigger.Auto, Engine = ScanEngine.AppleModel, AssetId = snapPhoto.Id, CreatedAt = SeedTime });
            await context.SaveChangesAsync();

            context.ScanCandidates.Add(new ScanCandidate
            {
                Id = candidateId,
                LibraryId = libraryId,
                SessionId = sessionId,
                Seq = 1,
                Source = CandidateSource.Barcode,
                SnapId = snapId,
                Resolution = CandidateResolution.Matched,
                MatchedReleaseId = releaseId,
                CreatedItemId = itemId,
                CreatedAt = SeedTime,
            });
            await context.SaveChangesAsync();

            context.MatchFeedback.Add(new MatchFeedback { Id = Guid.CreateVersion7(), LibraryId = libraryId, CandidateId = candidateId, CreatedAt = SeedTime });
            context.LibraryInvites.Add(new LibraryInvite
            {
                Id = Guid.CreateVersion7(),
                LibraryId = libraryId,
                Role = LibraryRole.Viewer,
                Name = "Pending guest",
                TokenHash = "hash-" + Guid.NewGuid().ToString("N"),
                CreatedBy = userId,
                CreatedAt = SeedTime,
                ExpiresAt = SeedTime.AddYears(10),
            });
            await context.SaveChangesAsync();
        }

        return new Seeded(libraryId, itemId, cover.Id, photo.Id, releaseId, candidateId, [cover.BlobPath, photo.BlobPath, snapPhoto.BlobPath]);
    }

    private async Task<Asset> SeedAssetAsync(Guid libraryId, AssetKind kind)
    {
        var id = Guid.CreateVersion7();
        var blobPath = $"{id.ToString("N")[..2]}/{id.ToString("N")[2..4]}/{id:N}.jpg";
        var file = Path.Combine(CoversRoot, blobPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, [0xFF, 0xD8, 0xFF, 0xD9]);

        var asset = new Asset
        {
            Id = id,
            Kind = kind,
            BlobPath = blobPath,
            ContentType = "image/jpeg",
            Sha256 = id.ToString("N") + id.ToString("N"),
            OwnerLibraryId = libraryId,
            CreatedAt = SeedTime,
        };
        await using var context = Db.CreateContext();
        context.Assets.Add(asset);
        await context.SaveChangesAsync();
        return asset;
    }

    private async Task<Guid> TestData_CreateReleaseAsync(Guid? ownerLibraryId)
    {
        var releaseId = await CollectionMaxxing.TestSupport.TestData.CreateReleaseAsync(Db, ownerLibraryId);
        if (ownerLibraryId is not null)
        {
            await using var context = Db.CreateContext();
            context.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = releaseId, Type = IdentifierType.Gtin13, Value = "0" + Random.Shared.NextInt64(100000000000, 999999999999) });
            await context.SaveChangesAsync();
        }

        return releaseId;
    }

    protected bool FileExists(string blobPath) => File.Exists(Path.Combine(CoversRoot, blobPath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Every table that has rows for the library, by name, with the count: inspects the whole schema (any table with a
    /// <c>library_id</c> column, plus <c>libraries</c>, and the <c>owner_library_id</c> of releases and assets). Empty means nothing is left.
    /// </summary>
    protected async Task<Dictionary<string, long>> RowsOfLibraryAsync(Guid libraryId)
    {
        var found = new Dictionary<string, long>();
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();

        var targets = new List<(string Table, string Column)> { ("libraries", "id") };
        await using (var command = new NpgsqlCommand(
            "select table_name, column_name from information_schema.columns where table_schema = 'public' and column_name in ('library_id', 'owner_library_id') and table_name <> 'audit_events'",
            connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                targets.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (table, column) in targets)
        {
            await using var count = new NpgsqlCommand($"select count(*) from \"{table}\" where \"{column}\" = @id", connection);
            count.Parameters.AddWithValue("id", libraryId);
            var rows = (long)(await count.ExecuteScalarAsync())!;
            if (rows > 0)
            {
                found[$"{table}.{column}"] = rows;
            }
        }

        return found;
    }

    /// <summary>Rows of the user's own (not their library's): sign-in material, devices, remembered requests.</summary>
    protected async Task<Dictionary<string, long>> PersonalRowsAsync(Guid userId)
    {
        var found = new Dictionary<string, long>();
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        var queries = new Dictionary<string, string>
        {
            ["user_passkeys"] = "select count(*) from user_passkeys where user_id = @id",
            ["device_pairings"] = "select count(*) from device_pairings where user_id = @id",
            ["web_sessions"] = "select count(*) from web_sessions where user_id = @id",
            ["idempotency_keys"] = "select count(*) from idempotency_keys where user_id = @id",
            ["library_members"] = "select count(*) from library_members where user_id = @id",
            ["user_roles"] = "select count(*) from user_roles where user_id = @id",
            ["openiddict_authorizations"] = "select count(*) from openiddict_authorizations where subject = @text",
            ["openiddict_tokens"] = "select count(*) from openiddict_tokens where subject = @text",
        };
        foreach (var (name, sql) in queries)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", userId);
            command.Parameters.AddWithValue("text", userId.ToString());
            var rows = (long)(await command.ExecuteScalarAsync())!;
            if (rows > 0)
            {
                found[name] = rows;
            }
        }

        return found;
    }
}
