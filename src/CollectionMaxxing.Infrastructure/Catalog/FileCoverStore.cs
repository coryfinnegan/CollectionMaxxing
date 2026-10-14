using CollectionMaxxing.Application.Catalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Infrastructure.Catalog;

/// <summary>Reads covers and photos from a folder on the PC. A blob path can never point outside that folder.</summary>
public sealed class FileCoverStore : ICoverStore
{
    private readonly string _root;

    public FileCoverStore(string root)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public Task<Stream?> OpenReadAsync(string blobPath, CancellationToken cancellationToken)
    {
        if (!BlobPaths.IsSafe(blobPath))
        {
            return Task.FromResult<Stream?>(null);
        }

        // Belt and braces: even a path that passed the check must resolve inside the root.
        var full = Path.GetFullPath(Path.Combine(_root, blobPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return Task.FromResult<Stream?>(null);
        }

        try
        {
            Stream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult<Stream?>(stream);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }
}

public static class CatalogInfrastructure
{
    /// <summary>Configuration key of the folder that holds cover files.</summary>
    public const string CoversRootKey = "Catalog:CoversRoot";

    /// <summary>Registers the cover store and the local catalogue search. The folder defaults to <c>data/covers</c> beside the app.</summary>
    public static IServiceCollection AddCatalogStorage(this IServiceCollection services)
    {
        services.AddScoped<ILocalCatalogSearch, LocalCatalogSearch>();
        return services.AddSingleton<ICoverStore>(provider =>
            new FileCoverStore(provider.GetRequiredService<IConfiguration>()[CoversRootKey] ?? Path.Combine("data", "covers")));
    }
}
