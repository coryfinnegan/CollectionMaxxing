using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Auth.KeyRing;

/// <summary>Settings under <c>Auth:KeyRing:</c>.</summary>
public sealed class KeyRingOptions
{
    public const string SectionName = "Auth:KeyRing";

    /// <summary>
    /// The folder (a mounted volume in the container) that holds ASP.NET's Data Protection key ring: the keys that seal the session
    /// cookie, the sign-in intent and Auth0 state. Required outside Development, so a redeploy can't quietly start with fresh keys and
    /// sign everybody out. In Development it defaults to <c>keyring</c> under <c>Auth:SecretsPath</c>.
    /// </summary>
    public string? Path { get; set; }
}

public static class KeyRingRegistration
{
    /// <summary>
    /// Keeps the Data Protection key ring in a configured folder, so cookies and ceremonies minted before a restart or a redeploy
    /// still validate after it. Startup fails, loudly, when Production has no folder (or it can't be written) rather than falling
    /// back to keys that die with the container. The keys themselves are never logged and never part of the image or the repo.
    /// </summary>
    public static IServiceCollection AddPersistedKeyRing(this IServiceCollection services)
    {
        services.AddOptions<KeyRingOptions>().BindConfiguration(KeyRingOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<KeyRingOptions>, KeyRingOptionsValidator>();

        // The application name keeps the ring's purpose the same wherever the app runs from.
        services.AddDataProtection().SetApplicationName("CollectionMaxxing");
        services.AddOptions<KeyManagementOptions>().Configure<IOptions<KeyRingOptions>, IOptions<AuthOptions>, IHostEnvironment, ILoggerFactory>(
            (keys, ring, auth, environment, loggers) =>
                keys.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(KeyRingOptionsValidator.FolderFor(ring.Value, auth.Value, environment)), loggers));
        return services;
    }
}

internal sealed class KeyRingOptionsValidator(IOptions<AuthOptions> auth, IHostEnvironment environment) : IValidateOptions<KeyRingOptions>
{
    /// <summary>The folder the keys live in. Throws when there is none and this environment insists on one.</summary>
    public static string FolderFor(KeyRingOptions ring, AuthOptions auth, IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(ring.Path))
        {
            return Path.GetFullPath(ring.Path.Trim());
        }

        return environment.IsDevelopment()
            ? Path.GetFullPath(Path.Combine(auth.SecretsPath, "keyring"))
            : throw new InvalidOperationException(MissingMessage);
    }

    private const string MissingMessage =
        "Auth:KeyRing:Path must be set outside Development: it is the folder (a mounted volume) where the Data Protection keys are kept, "
        + "so that sign-ins and sign-ins in progress survive a restart or a redeploy. Refusing to start with throwaway keys.";

    public ValidateOptionsResult Validate(string? name, KeyRingOptions options)
    {
        string folder;
        try
        {
            folder = FolderFor(options, auth.Value, environment);
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }

        try
        {
            // Make it now, and prove it can be written, so a read-only or missing volume fails at start-up and not on someone's login.
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ValidateOptionsResult.Fail($"Auth:KeyRing:Path can't be written ({ex.GetType().Name}). Check the volume and its permissions.");
        }

        return ValidateOptionsResult.Success;
    }
}
