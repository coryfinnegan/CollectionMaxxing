using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Infrastructure.ShelfReading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.ShelfReading;

public sealed class ShelfReaderRegistrationTests
{
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
