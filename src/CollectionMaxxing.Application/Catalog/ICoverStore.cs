namespace CollectionMaxxing.Application.Catalog;

/// <summary>Where cover and photo files live. Infrastructure implements it over the PC's file system.</summary>
public interface ICoverStore
{
    /// <summary>
    /// Opens the stored file for reading. Null when the file is missing or <paramref name="blobPath"/> is not a safe
    /// relative path inside the store; the caller can't tell the two apart, and neither can an attacker.
    /// </summary>
    Task<Stream?> OpenReadAsync(string blobPath, CancellationToken cancellationToken);
}

/// <summary>The one rule for what a stored blob path may look like, shared by the use case and every store.</summary>
public static class BlobPaths
{
    /// <summary>True for a plain relative path of simple segments ("ab/cd/ef01.jpg"): no rooting, no "..", no odd characters.</summary>
    public static bool IsSafe(string? blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || blobPath.Length > 500)
        {
            return false;
        }

        foreach (var segment in blobPath.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            {
                return false;
            }
        }

        return true;
    }
}
