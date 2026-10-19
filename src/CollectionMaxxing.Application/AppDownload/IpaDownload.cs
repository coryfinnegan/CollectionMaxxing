using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.AppDownload;

/// <summary>What the web app shows about the synced iPhone app: its size, when it was synced and, if the release had one, its SHA-256.</summary>
public sealed record IpaInfoDto(long SizeBytes, DateTimeOffset ModifiedAt, string? Sha256);

/// <summary>The info route's answer. "Not there yet" is a normal answer (<c>Available</c> false), not an error, so the web app's console stays quiet.</summary>
public sealed record IpaStatusDto(bool Available, long? SizeBytes, DateTimeOffset? ModifiedAt, string? Sha256);

/// <summary>The one iPhone app file the deployer syncs into the files volume. There is no way to ask for any other file.</summary>
public interface IIpaStore
{
    /// <summary>Null when no .ipa has been synced yet.</summary>
    IpaInfoDto? Describe();

    /// <summary>The file, ready to stream; null when there is none.</summary>
    Stream? OpenRead();
}

public static class IpaErrors
{
    public static readonly DomainError NotAvailable = new(ErrorKind.NotFound, "ipa.not_available", "The iPhone app isn't here yet. It appears after the next release.");
}

/// <summary>Use case: describe the iPhone app that can be downloaded.</summary>
public static class GetIpaInfo
{
    public sealed class Handler(IIpaStore store)
    {
        public IpaStatusDto Handle() =>
            store.Describe() is { } info ? new IpaStatusDto(true, info.SizeBytes, info.ModifiedAt, info.Sha256) : new IpaStatusDto(false, null, null, null);
    }
}

/// <summary>Use case: open the iPhone app for download.</summary>
public static class OpenIpa
{
    public sealed class Handler(IIpaStore store)
    {
        public (Stream Content, IpaInfoDto Info)? Handle()
        {
            if (store.Describe() is not { } info)
            {
                return null;
            }

            return store.OpenRead() is { } content ? (content, info) : null;
        }
    }
}
