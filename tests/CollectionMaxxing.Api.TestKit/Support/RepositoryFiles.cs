namespace CollectionMaxxing.Api.Tests.Support;

public static class RepositoryFiles
{
    /// <summary>Finds a file under the repository root (the folder that holds <c>CollectionMaxxing.slnx</c>).</summary>
    public static string Find(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CollectionMaxxing.slnx")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("Can't find the repository root (CollectionMaxxing.slnx) above the test binaries.")
            : Path.Combine([directory.FullName, .. relativePath]);
    }
}
