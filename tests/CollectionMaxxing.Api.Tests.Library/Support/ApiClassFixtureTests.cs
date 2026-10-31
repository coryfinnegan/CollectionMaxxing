namespace CollectionMaxxing.Api.Tests.Support;

public class ApiClassFixtureTests
{
    [Fact]
    public void Every_test_class_that_takes_the_api_fixture_gets_a_database_and_host_of_its_own()
    {
        ClassFixtureGuard.Offenders(typeof(ApiClassFixtureTests).Assembly)
            .ShouldBeEmpty("Derive from ApiTest, or declare IClassFixture<ApiFixture>, so the class gets its own database and API host.");
    }
}
