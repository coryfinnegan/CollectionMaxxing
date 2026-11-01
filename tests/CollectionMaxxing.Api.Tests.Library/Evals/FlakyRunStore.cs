using CollectionMaxxing.Application.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>The real store, except that it refuses to keep some groundings (and some header saves): a disk that fails under the PC.</summary>
internal sealed class FlakyRunStore(IEvalRunStore inner, Func<string, GroundedPhotoFile, bool> fails, Func<OpenRunHeader, bool>? failsSave) : IEvalRunStore
{
    public bool IsConfigured => inner.IsConfigured;

    public OpenRunHeader? Load() => inner.Load();

    public void Create(OpenRunHeader header) => inner.Create(header);

    public void Save(OpenRunHeader header)
    {
        if (failsSave?.Invoke(header) == true)
        {
            throw new IOException("the disk is full");
        }

        inner.Save(header);
    }

    public void Discard() => inner.Discard();

    public IReadOnlySet<string> StoredIds() => inner.StoredIds();

    public IReadOnlySet<string> GroundedIds() => inner.GroundedIds();

    public StoredEvalPhoto? LoadPhoto(string photoId) => inner.LoadPhoto(photoId);

    public void SavePhoto(string photoId, StoredEvalPhoto photo) => inner.SavePhoto(photoId, photo);

    public void DeletePhoto(string photoId) => inner.DeletePhoto(photoId);

    public GroundedPhotoFile? LoadGrounded(string photoId) => inner.LoadGrounded(photoId);

    public void SaveGrounded(string photoId, GroundedPhotoFile grounded)
    {
        if (fails(photoId, grounded))
        {
            throw new IOException("the disk is full");
        }

        inner.SaveGrounded(photoId, grounded);
    }

    public void DeleteGrounded(string photoId) => inner.DeleteGrounded(photoId);

    public void ClearPhotos() => inner.ClearPhotos();
}
