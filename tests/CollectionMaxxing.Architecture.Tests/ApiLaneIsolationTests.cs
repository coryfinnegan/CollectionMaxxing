namespace CollectionMaxxing.Architecture.Tests;

/// <summary>
/// The Api.Tests lanes (issue #297) each start their own Postgres container. Their test classes run in parallel, each class on a database
/// of its own cloned from that container's migrated template (ApiFixture, a class fixture of ApiTest): a lane without the server fixture
/// has no template to clone, and a lane that still forces CollectionPerAssembly runs every test one after another.
/// </summary>
public sealed class ApiLaneIsolationTests
{
    private static readonly string[] TestProjects =
    [
        "CollectionMaxxing.Api.Tests",
        "CollectionMaxxing.Api.Tests.Auth",
        "CollectionMaxxing.Api.Tests.Library",
        "CollectionMaxxing.Application.Tests",
        "CollectionMaxxing.Architecture.Tests",
        "CollectionMaxxing.Domain.Tests",
        "CollectionMaxxing.Infrastructure.Tests",
        "CollectionMaxxing.Recognition.Evals",
    ];

    [Fact]
    public void Api_lanes_give_every_test_class_its_own_database_and_run_the_classes_in_parallel()
    {
        var lanes = TestProjects.Where(name => name.StartsWith("CollectionMaxxing.Api.Tests", StringComparison.Ordinal)).ToList();
        lanes.Count.ShouldBe(3);
        foreach (var lane in lanes)
        {
            var assemblyInfo = File.ReadAllText(Path.Combine(RepoRoot(), "tests", lane, "AssemblyInfo.cs"));
            assemblyInfo.ShouldContain("AssemblyFixture(typeof(PostgresServerLifetime))");
            assemblyInfo.ShouldContain("CollectionBehavior(CollectionBehavior.CollectionPerClass)");
            assemblyInfo.ShouldNotContain("CollectionPerAssembly");
            assemblyInfo.ShouldNotContain("DisableTestParallelization");
        }
    }

    [Fact]
    public void The_gate_runs_every_test_project()
    {
        // scripts/check.* runs every tests/ project except the two support libraries; a project added, removed or
        // renamed here fails loudly instead of silently gaining or losing a lane.
        var root = RepoRoot();
        var onDisk = Directory.EnumerateDirectories(Path.Combine(root, "tests"))
            .Select(dir => Path.GetFileName(dir)!)
            .Where(name => name is not ("CollectionMaxxing.TestSupport" or "CollectionMaxxing.Api.TestKit"))
            .Where(name => File.Exists(Path.Combine(root, "tests", name, $"{name}.csproj")))
            .OrderBy(name => name)
            .ToList();
        onDisk.ShouldBe(TestProjects.OrderBy(name => name).ToList());
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CollectionMaxxing.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Can't find the repository root (CollectionMaxxing.slnx).");
    }
}
