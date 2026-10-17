using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Accounts.Deletion;
using CollectionMaxxing.Infrastructure.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Photo;

/// <summary>
/// A photo file that can't be deleted right now (a virus scanner has it open, say) is never forgotten: its record stays, and the daily
/// clean-up job deletes the file once it lets go.
/// </summary>
public class PhotoStuckFileTests : PhotoTest
{
    private readonly StuckableStore _store;

    public PhotoStuckFileTests(ApiFixture api)
        : base(api)
    {
        _store = new StuckableStore(new FilePhotoStore(PhotosRoot, new FileAccountFiles(PhotosRoot)));
        ConfigureServices = services => services.AddSingleton<IPhotoStore>(_store);
    }

    private async Task RunCleanUpAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CleanUpScanSessions>().HandleAsync(new CleanUpScanSessions.Job(), CancellationToken.None);
    }

    [Fact]
    public async Task An_abandoned_sessions_stuck_photo_file_is_deleted_by_the_next_clean_up()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);
        (await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var photo = (await PhotoOfAsync(candidateId)).ShouldNotBeNull();

        _store.Stuck = true;
        (await editor.DeleteAsync(SessionUrl(sessionId))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        StoredFileExists(photo).ShouldBeTrue();
        (await AssetsOfAsync(library.Id)).ShouldHaveSingleItem().Id.ShouldBe(photo.Id); // the record that lets the clean-up find the file

        await RunCleanUpAsync();
        StoredFileExists(photo).ShouldBeTrue("still stuck: tried again tomorrow");
        (await AssetsOfAsync(library.Id)).Count.ShouldBe(1);

        _store.Stuck = false;
        await RunCleanUpAsync();
        StoredFileExists(photo).ShouldBeFalse();
        (await AssetsOfAsync(library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_replaced_photos_stuck_file_is_deleted_by_the_next_clean_up_and_the_new_photo_stays()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);
        (await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var first = (await PhotoOfAsync(candidateId)).ShouldNotBeNull();

        _store.Stuck = true;
        (await UploadAsync(editor, sessionId, candidateId, JpegFixtures.WithSize(16, 16))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = (await PhotoOfAsync(candidateId)).ShouldNotBeNull();
        StoredFileExists(first).ShouldBeTrue();

        _store.Stuck = false;
        await RunCleanUpAsync();

        StoredFileExists(first).ShouldBeFalse();
        StoredFileExists(second).ShouldBeTrue();
        (await AssetsOfAsync(library.Id)).ShouldHaveSingleItem().Id.ShouldBe(second.Id);
    }

    [Fact]
    public async Task A_rejected_candidates_stuck_photo_file_is_deleted_by_the_next_clean_up_and_the_accepted_cover_stays()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, kept, _) = await ScanOneAsync(library);
        var rejected = await ScanAnotherAsync(library, sessionId, 2);
        using var editor = As(library, LibraryRole.Editor);
        (await UploadAsync(editor, sessionId, kept, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UploadAsync(editor, sessionId, rejected, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DecideAsync(editor, sessionId, rejected, new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var cover = (await PhotoOfAsync(kept)).ShouldNotBeNull();
        var dropped = (await PhotoOfAsync(rejected)).ShouldNotBeNull();

        _store.Stuck = true;
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        StoredFileExists(dropped).ShouldBeTrue();

        _store.Stuck = false;
        await RunCleanUpAsync();

        StoredFileExists(dropped).ShouldBeFalse();
        StoredFileExists(cover).ShouldBeTrue();
        (await AssetsOfAsync(library.Id)).ShouldHaveSingleItem().Id.ShouldBe(cover.Id);
    }

    /// <summary>A photo store whose deletes can be made to fail, the way a locked file does.</summary>
    private sealed class StuckableStore(IPhotoStore inner) : IPhotoStore
    {
        public volatile bool Stuck;

        public Task SaveAsync(string blobPath, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            inner.SaveAsync(blobPath, bytes, cancellationToken);

        public Task<bool> TryDeleteAsync(string blobPath, CancellationToken cancellationToken) =>
            Stuck ? Task.FromResult(false) : inner.TryDeleteAsync(blobPath, cancellationToken);
    }
}
