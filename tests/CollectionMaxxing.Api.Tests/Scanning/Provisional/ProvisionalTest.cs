using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning.Identify;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Provisional;

/// <summary>Base of the provisional-record tests (#345): a throwaway photo folder, crop-upload and keep helpers, and an unreadable spine to keep.</summary>
public abstract class ProvisionalTest : IdentifyTest
{
    protected ProvisionalTest(ApiFixture api)
        : base(api)
    {
        Settings["Catalog:CoversRoot"] = PhotosRoot;
    }

    protected string PhotosRoot { get; } = Path.Combine(Path.GetTempPath(), "cm-crops-" + Guid.NewGuid().ToString("N"));

    public override async ValueTask DisposeAsync()
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
        await base.DisposeAsync();
    }

    protected static string CropUrl(Guid sessionId, Guid candidateId) => $"{CandidateUrl(sessionId, candidateId)}/crop";

    protected static string ProvisionalsUrl(Guid sessionId) => $"{SessionUrl(sessionId)}/provisionals";

    protected static string LibraryProvisionalsUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/provisionals";

    protected static string ProvisionalUrl(Guid libraryId, Guid provisionalId) => $"{LibraryProvisionalsUrl(libraryId)}/{provisionalId}";

    /// <summary>"Use my photo" for the cited-snap tests: a photo snap the keep then cites.</summary>
    protected static async Task<HttpResponseMessage> UploadPhotoAsync(
        HttpClient client, Guid sessionId, Guid candidateId, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "photo.jpg");
        return await client.PostAsync($"{CandidateUrl(sessionId, candidateId)}/photo", form);
    }

    /// <summary>Uploads <paramref name="bytes"/> as the multipart part <c>file</c> the way the app does.</summary>
    protected static async Task<HttpResponseMessage> UploadCropAsync(
        HttpClient client, Guid sessionId, Guid candidateId, byte[] bytes, string fileName = "segment-crop.jpg")
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", fileName);
        return await client.PostAsync(CropUrl(sessionId, candidateId), form);
    }

    protected static async Task<HttpResponseMessage> KeepAsync(HttpClient client, Guid sessionId, object body) =>
        await client.PostAsync(ProvisionalsUrl(sessionId), Json(body));

    /// <summary>Reads one picture the catalogue cannot name: a T2 row with the phone's read text, still undecided.</summary>
    protected static async Task<List<JsonElement>> IdentifyUnreadableAsync(HttpClient client, Guid sessionId, string text = "Zzxq Unreadable Spine Qqz")
    {
        var response = await IdentifyAsync(client, sessionId, null, Read(title: text, text: text));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
    }

    protected async Task<ProvisionalRecord?> ProvisionalAsync(Guid provisionalId)
    {
        await using var context = Db.CreateContext();
        return await context.ProvisionalRecords.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(p => p.Id == provisionalId);
    }

    protected async Task<List<ProvisionalRecord>> ProvisionalsOfAsync(Guid libraryId)
    {
        await using var context = Db.CreateContext();
        return await context.ProvisionalRecords.IgnoreQueryFilters().AsNoTracking().Where(p => p.LibraryId == libraryId).ToListAsync();
    }

    protected async Task<Asset?> AssetAsync(Guid assetId)
    {
        await using var context = Db.CreateContext();
        return await context.Assets.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(a => a.Id == assetId);
    }

    protected async Task<ScanCandidate> CandidateAsync(Guid candidateId)
    {
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == candidateId);
    }

    protected byte[] StoredBytes(Asset asset) => File.ReadAllBytes(Path.Combine(PhotosRoot, asset.BlobPath.Replace('/', Path.DirectorySeparatorChar)));

    protected bool StoredFileExists(Asset asset) => File.Exists(Path.Combine(PhotosRoot, asset.BlobPath.Replace('/', Path.DirectorySeparatorChar)));

    protected string[] FilesOnDisk() =>
        Directory.Exists(PhotosRoot) ? Directory.GetFiles(PhotosRoot, "*", SearchOption.AllDirectories) : [];
}
