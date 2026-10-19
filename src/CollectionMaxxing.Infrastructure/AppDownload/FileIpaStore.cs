using System.Text.RegularExpressions;
using CollectionMaxxing.Application.AppDownload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Infrastructure.AppDownload;

/// <summary>
/// Reads <c>ipa/CollectionMaxxing.ipa</c> (and its <c>.sha256</c>) from the files volume. Both names are fixed here: no request value ever reaches a path.
/// </summary>
public sealed partial class FileIpaStore(string filesRoot) : IIpaStore
{
    public const string FilesRootKey = "Files:Root";
    private const string FileName = "CollectionMaxxing.ipa";

    private readonly string _path = Path.Combine(Path.GetFullPath(filesRoot), "ipa", FileName);

    public IpaInfoDto? Describe()
    {
        try
        {
            var file = new FileInfo(_path);
            return file.Exists ? new IpaInfoDto(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), ReadChecksum()) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Stream? OpenRead()
    {
        try
        {
            return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The deployer writes "&lt;hash&gt;  CollectionMaxxing.ipa". Anything that doesn't start with 64 hex digits is ignored.</summary>
    private string? ReadChecksum()
    {
        try
        {
            var match = Hash().Match(File.ReadAllText(_path + ".sha256"));
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\A\s*([0-9a-fA-F]{64})(?![0-9a-fA-F])")]
    private static partial Regex Hash();
}

public static class IpaInfrastructure
{
    /// <summary>The files volume defaults to <c>data/files</c> beside the app; the compose file sets <c>Files__Root</c>.</summary>
    public static IServiceCollection AddIpaStorage(this IServiceCollection services)
    {
        services.TryAddSingleton<IIpaStore>(provider =>
            new FileIpaStore(provider.GetRequiredService<IConfiguration>()[FileIpaStore.FilesRootKey] ?? Path.Combine("data", "files")));
        return services;
    }
}
