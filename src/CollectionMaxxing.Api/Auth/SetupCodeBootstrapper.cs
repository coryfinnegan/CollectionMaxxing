using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// On start, if there is no owner yet, makes a single-use setup code (24 hours) and shows it to whoever runs the server: in the
/// log and in <c>secrets/setup-code.txt</c>. The database may still be starting, so it retries for a while instead of crashing.
/// </summary>
public sealed partial class SetupCodeBootstrapper(
    IServiceScopeFactory scopes,
    ISetupCodeAnnouncer announcer,
    IOptions<AuthOptions> options,
    ILogger<SetupCodeBootstrapper> logger) : BackgroundService
{
    private const int Attempts = 20;
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Setup.IssueOnStartup)
        {
            return;
        }

        for (var attempt = 1; attempt <= Attempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var issued = await scope.ServiceProvider.GetRequiredService<IssueCode.Handler>().HandleAsync(SetupCodePurpose.Setup, stoppingToken);
                if (issued is not null)
                {
                    await announcer.AnnounceAsync(issued.Code, issued.ExpiresAt, stoppingToken);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogNotReady(attempt, Attempts, ex.GetType().Name);
                await Task.Delay(Pause, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        LogGaveUp();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't make the setup code yet (attempt {Attempt} of {Attempts}, {Reason}). Is the database up and migrated?")]
    private partial void LogNotReady(int attempt, int attempts, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "No setup code was made. Fix the database, then restart the server.")]
    private partial void LogGaveUp();
}
