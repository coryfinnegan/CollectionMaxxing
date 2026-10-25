namespace CollectionMaxxing.Architecture.Tests;

/// <summary>
/// The Api.Tests lanes (issue #297) each run serially against their own Postgres: a lane without the assembly
/// fixture or the CollectionPerAssembly behaviour would run tests in parallel against one shared database.
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
    public void Api_lanes_run_serially_against_their_own_database()
    {
        var lanes = TestProjects.Where(name => name.StartsWith("CollectionMaxxing.Api.Tests", StringComparison.Ordinal)).ToList();
        lanes.Count.ShouldBe(3);
        foreach (var lane in lanes)
        {
            var assemblyInfo = File.ReadAllText(Path.Combine(RepoRoot(), "tests", lane, "AssemblyInfo.cs"));
            assemblyInfo.ShouldContain("AssemblyFixture(typeof(ApiFixture))");
            assemblyInfo.ShouldContain("CollectionBehavior(CollectionBehavior.CollectionPerAssembly)");
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
