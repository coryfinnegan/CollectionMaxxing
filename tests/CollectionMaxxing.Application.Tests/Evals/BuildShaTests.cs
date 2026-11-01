using CollectionMaxxing.Application.Evals;

namespace CollectionMaxxing.Application.Tests.Evals;

/// <summary>
/// The git sha that names a result file. The compose file passes the deployed commit to the api as the environment variable
/// <c>CM_BUILD_SHA</c> (the same value <c>/health/version</c> reports); these tests read it back the way <see cref="BuildSha"/> does.
/// </summary>
public class BuildShaTests
{
    private const string Variable = "CM_BUILD_SHA";

    [Fact]
    public void With_CM_BUILD_SHA_set_the_result_file_uses_it()
    {
        Environment.SetEnvironmentVariable(Variable, "abc123def456ghi789");
        try
        {
            BuildSha.Resolve(new EvalsOptions()).ShouldBe("abc123def456");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, null);
        }
    }

    [Fact]
    public void Evals_BuildSha_wins_over_CM_BUILD_SHA()
    {
        Environment.SetEnvironmentVariable(Variable, "fromenvironment");
        try
        {
            BuildSha.Resolve(new EvalsOptions { BuildSha = "configured" }).ShouldBe("configured");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, null);
        }
    }

}
