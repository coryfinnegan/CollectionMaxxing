namespace CollectionMaxxing.Application.Scanning.Photo;

/// <summary>Where the photos people upload are kept: a folder on the PC (the same one the covers are served from). Implemented by Infrastructure.</summary>
public interface IPhotoStore
{
    /// <summary>
    /// Writes <paramref name="bytes"/> at <paramref name="blobPath"/>, a safe relative path (<see cref="Catalog.BlobPaths.IsSafe"/>). Nothing is
    /// left at that path unless the whole file was written, and the call never overwrites an existing file.
    /// </summary>
    Task SaveAsync(string blobPath, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Deletes the file. True when it is gone (a file that was never there counts); false when it is stuck and still on disk.</summary>
    Task<bool> TryDeleteAsync(string blobPath, CancellationToken cancellationToken);
}
