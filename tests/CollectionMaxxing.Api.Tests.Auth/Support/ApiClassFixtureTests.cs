namespace CollectionMaxxing.Api.Tests.Support;

public class ApiClassFixtureTests
{
    [Fact]
    public void Every_test_class_that_takes_the_api_fixture_gets_a_database_and_host_of_its_own()
    {
        // MatrixCompletenessTests is locked and only reads the app's endpoint metadata: it uses the assembly's shared ApiFixture.
        ClassFixtureGuard.Offenders(typeof(ApiClassFixtureTests).Assembly, allowed: "MatrixCompletenessTests")
            .ShouldBeEmpty("Derive from ApiTest, or declare IClassFixture<ApiFixture>, so the class gets its own database and API host.");
    }
}
