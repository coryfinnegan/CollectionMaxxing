using System.Text.Json;
using System.Text.RegularExpressions;
using CollectionMaxxing.Application.AppDownload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Infrastructure.AppDownload;

/// <summary>
/// Reads <c>ipa/CollectionMaxxing.ipa</c> (and its <c>.sha256</c> and <c>version.json</c>) from the files volume. The names are fixed here: no request value ever reaches a path.
/// </summary>
public sealed partial class FileIpaStore(string filesRoot) : IIpaStore
{
    public const string FilesRootKey = "Files:Root";
    private const string FileName = "CollectionMaxxing.ipa";
    private const string VersionFileName = "version.json";

    private readonly string _path = Path.Combine(Path.GetFullPath(filesRoot), "ipa", FileName);

    public IpaInfoDto? Describe()
    {
        try
        {
            var file = new FileInfo(_path);
            if (!file.Exists)
            {
                return null;
            }

            var checksum = ReadChecksum();
            return new IpaInfoDto(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), checksum, ReadVersion(checksum));
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

    /// <summary>
    /// The deployer writes <c>{"version":"1.0.2","sha256":"&lt;hash&gt;"}</c> next to the file. The version only counts when its checksum is the one
    /// of the file being served, so a version file left over from an earlier sync is never reported. Anything odd gives null, never an error.
    /// </summary>
    private string? ReadVersion(string? ipaChecksum)
    {
        if (ipaChecksum is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(_path)!, VersionFileName)));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String
                || !document.RootElement.TryGetProperty("sha256", out var sha256) || sha256.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = version.GetString()!;
            return string.Equals(sha256.GetString(), ipaChecksum, StringComparison.OrdinalIgnoreCase) && Semver().IsMatch(text) ? text : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\A\s*([0-9a-fA-F]{64})(?![0-9a-fA-F])")]
    private static partial Regex Hash();

    /// <summary>1.2.3 or 1.2.3-rc.1: the only shape the app compares.</summary>
    [GeneratedRegex(@"\A\d{1,9}\.\d{1,9}\.\d{1,9}(-[0-9A-Za-z][0-9A-Za-z.-]{0,31})?\z")]
    private static partial Regex Semver();
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
