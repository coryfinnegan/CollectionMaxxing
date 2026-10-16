namespace CollectionMaxxing.Architecture.Tests;

/// <summary>
/// Test servers listen on loopback only. A server on every interface makes Windows Firewall ask the owner about each new
/// test executable, and every agent worktree builds a new one (xUnit v3 test projects run as their own .exe).
/// </summary>
public sealed class TestServerBindingTests
{
    private static readonly string[] AnyInterface = ["WireMockServer.Start(", "ListenAnyIP(", "\"http://*:", "\"http://+:", "\"http://0.0.0.0:", "IPAddress.Any,", "IPAddress.IPv6Any"];

    [Fact]
    public void Tests_never_start_a_server_on_every_interface()
    {
        var root = RepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file) && Path.GetFileName(file) is not ("LoopbackWireMock.cs" or nameof(TestServerBindingTests) + ".cs"))
            .SelectMany(file => AnyInterface
                .Where(File.ReadAllText(file).Contains)
                .Select(pattern => $"{Path.GetRelativePath(root, file)}: {pattern}"))
            .ToList();

        offenders.ShouldBeEmpty("Start WireMock with LoopbackWireMock.Start() and bind any other test server to 127.0.0.1.");
    }

    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}bin{separator}", StringComparison.Ordinal) || file.Contains($"{separator}obj{separator}", StringComparison.Ordinal);
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
