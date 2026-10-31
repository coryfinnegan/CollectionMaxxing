using System.Diagnostics;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace CollectionMaxxing.TestSupport;

/// <summary>What <see cref="PostgresFixture"/> and <see cref="PostgresServer"/> share: the container, the migrations, Respawn and the perf line.</summary>
internal static class PostgresSupport
{
    /// <summary>
    /// Testcontainers asks for a newer Docker API than old daemons (e.g. Docker Desktop 24, API 1.43) accept,
    /// and Docker 29+ refuses anything older than 1.44. Pin the client to whatever the local daemon reports,
    /// unless the caller already chose a version. Called from the static constructor of each fixture.
    /// </summary>
    public static void PinDockerApiVersion()
    {
        if (Environment.GetEnvironmentVariable("DOCKER_API_VERSION") is null && TryGetDaemonApiVersion() is { } version)
        {
            Environment.SetEnvironmentVariable("DOCKER_API_VERSION", version);
        }
    }

    private static string? TryGetDaemonApiVersion()
    {
        try
        {
            using var docker = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "version --format {{.Server.APIVersion}}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (docker is null || !docker.WaitForExit(TimeSpan.FromSeconds(15)) || docker.ExitCode != 0)
            {
                return null;
            }

            var version = docker.StandardOutput.ReadToEnd().Trim();
            return version.Length > 0 ? version : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // no docker CLI on PATH: let Testcontainers use its default
        }
    }

    public static PostgreSqlContainer BuildContainer() => new PostgreSqlBuilder("postgres:17").Build();

    /// <summary>Applies the real migrations, so every test run also proves they work.</summary>
    public static async Task MigrateAsync(string connectionString)
    {
        await using var context = new AppDbContext(AppDbContextOptions.Create(connectionString), new CurrentUser());
        await context.Database.MigrateAsync();
    }

    /// <summary>A Respawn reset for the database behind <paramref name="connectionString"/> (the migration history stays).</summary>
    public static async Task<Respawner> CreateRespawnerAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Respawn.Graph.Table("__EFMigrationsHistory")],
        });
    }

    /// <summary>
    /// One line for the quality gate's report: check.* sets CM_PERF_LOG, and the gate counts the containers a run
    /// starts and their cost. (Test output never reaches the gate's logs: the console logger swallows it.)
    /// </summary>
    public static async Task WritePerfLineAsync(long started, long containerUp, long ready)
    {
        if (Environment.GetEnvironmentVariable("CM_PERF_LOG") is not { Length: > 0 } path)
        {
            return;
        }

        // Parallel lanes append from several processes at once; concurrent appends can hit a
        // sharing-violation IOException on Windows. Retry briefly, then give up: a lost perf
        // line is cosmetic, a failed lane is not.
        var line = $"[cm-perf] postgres ready in {Seconds(started, ready)}s (container {Seconds(started, containerUp)}s, migrate {Seconds(containerUp, ready)}s){Environment.NewLine}";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.AppendAllText(path, line);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(50);
            }
            catch (IOException)
            {
                return;
            }
        }
    }

    private static string Seconds(long from, long to) => ((to - from) / (double)Stopwatch.Frequency).ToString("F1");
}
