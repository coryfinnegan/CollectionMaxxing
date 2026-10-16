using System.Net.Http.Headers;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Photo;

/// <summary>Base of the cover-photo and feature-print tests: a throwaway photo folder, an upload helper, and a scanned candidate to upload for.</summary>
public abstract class PhotoTest : ScanTest
{
    protected PhotoTest(ApiFixture api)
        : base(api)
    {
        Settings["Catalog:CoversRoot"] = PhotosRoot;
    }

    protected string PhotosRoot { get; } = Path.Combine(Path.GetTempPath(), "cm-photos-" + Guid.NewGuid().ToString("N"));

    public override ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(PhotosRoot))
            {
                Directory.Delete(PhotosRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }

        GC.SuppressFinalize(this);
        return base.DisposeAsync();
    }

    protected static string PhotoUrl(Guid sessionId, Guid candidateId) => $"{CandidateUrl(sessionId, candidateId)}/photo";

    protected static string FeaturePrintsUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/feature-prints";

    /// <summary>Uploads <paramref name="bytes"/> as the multipart part <paramref name="part"/> the way the app does.</summary>
    protected static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid sessionId, Guid candidateId, byte[] bytes, string fileName = "crop.jpg", string contentType = "image/jpeg", string part = "file")
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, part, fileName);
        return await client.PostAsync(PhotoUrl(sessionId, candidateId), form);
    }

    /// <summary>A catalogue release, a session started by <paramref name="role"/> and one scanned candidate in it (matched and accepted).</summary>
    protected async Task<(Guid SessionId, Guid CandidateId, Guid ReleaseId)> ScanOneAsync(TestLibrary library, LibraryRole role = LibraryRole.Editor, int number = 1)
    {
        var code = ScanData.Upc(7000 + number);
        var releaseId = await CatalogAsync($"Photo test {number}", code);
        using var client = As(library, role);
        var sessionId = await StartSessionAsync(client, library.Id);
        var candidates = await ScanAsync(client, sessionId, code);
        return (sessionId, Id(candidates[0]), releaseId);
    }

    /// <summary>An extra candidate in an existing session.</summary>
    protected async Task<Guid> ScanAnotherAsync(TestLibrary library, Guid sessionId, int number)
    {
        var code = ScanData.Upc(7000 + number);
        await CatalogAsync($"Photo test {number}", code);
        using var client = As(library, LibraryRole.Editor);
        return Id((await ScanAsync(client, sessionId, code))[0]);
    }

    protected async Task<List<Asset>> AssetsOfAsync(Guid libraryId)
    {
        await using var context = Db.CreateContext();
        return await context.Assets.IgnoreQueryFilters().AsNoTracking().Where(a => a.OwnerLibraryId == libraryId).ToListAsync();
    }

    protected async Task<ScanCandidate> CandidateAsync(Guid candidateId)
    {
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == candidateId);
    }

    /// <summary>The asset the candidate's photo was stored as, via its snap. Null when it has none.</summary>
    protected async Task<Asset?> PhotoOfAsync(Guid candidateId)
    {
        await using var context = Db.CreateContext();
        var snapId = await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.Id == candidateId).Select(c => c.SnapId).SingleAsync();
        if (snapId is null)
        {
            return null;
        }

        return await (from snap in context.ScanSnaps.IgnoreQueryFilters()
                      where snap.Id == snapId && snap.AssetId != null
                      join asset in context.Assets.IgnoreQueryFilters() on snap.AssetId equals asset.Id
                      select asset).AsNoTracking().SingleOrDefaultAsync();
    }

    protected byte[] StoredBytes(Asset asset) => File.ReadAllBytes(Path.Combine(PhotosRoot, asset.BlobPath.Replace('/', Path.DirectorySeparatorChar)));

    protected bool StoredFileExists(Asset asset) => File.Exists(Path.Combine(PhotosRoot, asset.BlobPath.Replace('/', Path.DirectorySeparatorChar)));

    protected string[] FilesOnDisk() =>
        Directory.Exists(PhotosRoot) ? Directory.GetFiles(PhotosRoot, "*", SearchOption.AllDirectories) : [];

    protected async Task<Item> ItemAsync(Guid itemId)
    {
        await using var context = Db.CreateContext();
        return await context.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == itemId);
    }
}
