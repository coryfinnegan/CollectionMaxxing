using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Infrastructure;
using CollectionMaxxing.Infrastructure.ShelfReading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.ShelfReading;

public sealed class ShelfReaderRegistrationTests
{
    [Theory]
    [InlineData("Endpoint", "not a url")]
    [InlineData("Endpoint", "localhost:8081")]
    [InlineData("Endpoint", "ftp://127.0.0.1/")]
    [InlineData("MaxTokens", "0")]
    [InlineData("MaxLongEdge", "0")]
    [InlineData("TimeoutSeconds", "0")]
    [InlineData("MaxRepeats", "0")]
    [InlineData("SecondLookMaxTokens", "0")]
    public async Task A_bad_setting_fails_the_options_check_and_the_reader_then_reports_unavailable(string name, string value)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"ShelfReader:{name}"] = value }).Build());
        services.AddShelfReader();
        await using var provider = services.BuildServiceProvider();

        Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(() => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ShelfReaderOptions>>().Value);
        var result = await provider.GetRequiredService<IShelfReader>().ReadAsync([1], default);
        result.Status.ShouldBe(ShelfReadStatus.Unavailable);
    }

    [Fact]
    public async Task Registering_twice_adds_one_reader_and_with_no_configuration_it_is_off()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddShelfReader().AddShelfReader();

        services.Count(d => d.ServiceType == typeof(IShelfReader)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(IShelfStillMaker)).ShouldBe(1);
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IShelfReader>().ReadAsync([1], default);
        result.Status.ShouldBe(ShelfReadStatus.Off);
        provider.GetRequiredService<IShelfStillMaker>().ShouldBeOfType<ShelfStillMaker>();
    }

    [Fact]
    public async Task The_readers_own_client_has_an_infinite_timeout_because_the_reader_enforces_its_own()
    {
        // Composed without ConfigureHttpClientDefaults, so the standard resilience handler (which also sets an infinite timeout) is not what is asserted.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddShelfReader();
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IShelfReader)).Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void The_infrastructure_registers_the_shelf_reader_once_for_the_scan_endpoint_and_the_evals_to_share()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddInfrastructure();
        services.AddShelfReader(); // what the evals used to do themselves: still safe

        services.Count(d => d.ServiceType == typeof(IShelfReader)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(IShelfStillMaker)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(IShelfReaderSetup)).ShouldBe(1);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8081", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("not a url", false)] // named, but it fails the options check: a read would be "unavailable", so the phone is told there is no reader
    public void A_reader_is_set_up_when_a_valid_endpoint_is_named(string endpoint, bool expected) =>
        SetupWith(new Dictionary<string, string?> { ["ShelfReader:Endpoint"] = endpoint }).IsSetUp.ShouldBe(expected);

    [Fact]
    public void A_reader_with_a_bad_setting_is_not_set_up()
    {
        SetupWith(new Dictionary<string, string?> { ["ShelfReader:Endpoint"] = "http://127.0.0.1:8081", ["ShelfReader:TimeoutSeconds"] = "0" }).IsSetUp.ShouldBeFalse();
    }

    private static IShelfReaderSetup SetupWith(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddShelfReader();
        return services.BuildServiceProvider().GetRequiredService<IShelfReaderSetup>();
    }
}
