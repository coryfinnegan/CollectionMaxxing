using CollectionMaxxing.Infrastructure.Providers;
using CollectionMaxxing.Infrastructure.Providers.OpenLibrary;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>
/// The deploy compose file hands the api the Open Library contact email as the environment variable
/// <c>Providers__OpenLibrary__ContactEmail</c>. These tests read it back the way the host does, through the environment-variable
/// configuration source and the registration the app uses, so a renamed option or section would be caught here, not on the PC.
/// </summary>
public class OpenLibraryContactEmailTests
{
    // The configuration source takes only variables with this prefix, which keeps the test off every other variable of the process.
    private const string Prefix = "CMTEST_OPENLIBRARY_";
    private const string Variable = Prefix + "Providers__OpenLibrary__ContactEmail";

    private static OpenLibraryOptions OptionsWith(string? variableValue)
    {
        Environment.SetEnvironmentVariable(Variable, variableValue);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddEnvironmentVariables(Prefix).Build());
            services.AddCatalogProviderAdapters();
            using var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IOptions<OpenLibraryOptions>>().Value;
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, null);
        }
    }

    [Fact]
    public void The_contact_email_in_the_environment_names_the_client_to_open_library()
    {
        var options = OptionsWith("someone@example.test");

        options.ContactEmail.ShouldBe("someone@example.test");
        OpenLibraryProvider.UserAgent(options).ShouldBe("CollectionMaxxing/1.0 (someone@example.test)");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void With_no_contact_email_the_client_is_anonymous(string? variableValue)
    {
        var options = OptionsWith(variableValue);

        string.IsNullOrWhiteSpace(options.ContactEmail).ShouldBeTrue();
        OpenLibraryProvider.UserAgent(options).ShouldBe("CollectionMaxxing/1.0");
    }
}
