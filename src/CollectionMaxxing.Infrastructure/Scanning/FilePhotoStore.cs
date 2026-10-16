using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Scanning.Photo;

namespace CollectionMaxxing.Infrastructure.Scanning;

/// <summary>
/// Keeps uploaded photos in the PC's cover folder (the one <c>/media/covers</c> serves from). A path can never point outside the folder.
/// A file is written under a temporary name and moved into place, so a crash or a full disk never leaves half a photo at its real path.
/// Deleting goes through <see cref="IAccountFiles"/>, which already knows how to deal with a file that is locked for a moment.
/// </summary>
public sealed class FilePhotoStore : IPhotoStore
{
    private readonly string _root;
    private readonly IAccountFiles _files;

    public FilePhotoStore(string root, IAccountFiles files)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _files = files;
    }

    public async Task SaveAsync(string blobPath, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var full = Resolve(blobPath) ?? throw new ArgumentException("That is not a safe path inside the photo folder.", nameof(blobPath));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, full, overwrite: false);
        }
        catch
        {
            TryRemove(temporary);
            throw;
        }
    }

    public Task<bool> TryDeleteAsync(string blobPath, CancellationToken cancellationToken) =>
        Resolve(blobPath) is null ? Task.FromResult(true) : _files.TryDeleteAsync(blobPath, cancellationToken);

    private string? Resolve(string blobPath)
    {
        if (!BlobPaths.IsSafe(blobPath))
        {
            return null;
        }

        // Belt and braces: even a path that passed the check must resolve inside the root.
        var full = Path.GetFullPath(Path.Combine(_root, blobPath.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    private static void TryRemove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A temporary file nobody will ever read; the next restart's folder scan is not needed for it.
        }
        catch (UnauthorizedAccessException)
        {
            // as above
        }
    }
}
