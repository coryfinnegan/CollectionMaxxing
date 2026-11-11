using CollectionMaxxing.Application.ShelfReading;
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
}
