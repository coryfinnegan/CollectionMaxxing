using System.Globalization;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Accounts;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Commands for the person at the PC, run as arguments of the API itself (docs/07, "Recovery"):
/// <c>dotnet run --project src/CollectionMaxxing.Api -- --recovery-code</c>, or <c>cm-admin recovery-code</c> in the container.
/// Physical access to the PC is the recovery factor. A command prints its result and the program exits without serving.
/// </summary>
public static class AdminCommands
{
    public const string RecoveryCodeFlag = "--recovery-code";

    /// <summary>Runs the command named in <paramref name="args"/>, if any. Returns true when it did, so the caller can exit.</summary>
    public static async Task<bool> TryRunAdminCommandAsync(this WebApplication app, string[] args)
    {
        if (!args.Contains(RecoveryCodeFlag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        Environment.ExitCode = await RunRecoveryCodeAsync(app.Services, Console.Out, Console.Error, CancellationToken.None);
        return true;
    }

    /// <summary>Makes a recovery code for the owner and prints it. Returns the process exit code: 0 when a code was printed.</summary>
    public static async Task<int> RunRecoveryCodeAsync(IServiceProvider services, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            if (!await IssueCode.OwnerExistsAsync(db, cancellationToken))
            {
                await error.WriteLineAsync("There is no owner to recover yet. Use the setup code instead (see secrets/setup-code.txt, or restart the server).");
                return 1;
            }

            var issued = await scope.ServiceProvider.GetRequiredService<IssueCode.Handler>().HandleAsync(SetupCodePurpose.Recovery, cancellationToken);
            await output.WriteLineAsync(
                $"Recovery code: {issued!.Code}{Environment.NewLine}"
                + $"Open the web app's sign-in page, choose \"Can't sign in?\" and enter it, then sign in with the account you want to use. It works once and expires at {issued.ExpiresAt.ToString("u", CultureInfo.InvariantCulture)}.");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            await error.WriteLineAsync($"Couldn't make a recovery code: {ex.Message}");
            return 1;
        }
    }
}
