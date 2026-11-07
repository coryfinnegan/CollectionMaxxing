namespace CollectionMaxxing.Infrastructure.Evals;

/// <summary>
/// File access for the eval stores that tolerates other handles on the same file. On Windows a move over a file fails while any other handle has it
/// open without <see cref="FileShare.Delete"/> (the default for <see cref="File.ReadAllText(string)"/>, which the stores' own readers, the worker's
/// re-ground and antivirus scanners all use), and a read fails for the instant a move is replacing the file; Linux allows both. So reads open with full
/// sharing and both reads and replaces retry briefly (bounded) before giving up.
/// </summary>
internal static class EvalFileIo
{
    private const int Attempts = 12;
    private const int FirstDelayMs = 10;
    private const int MaxDelayMs = 250;

    public static string ReadAllText(string path) => Retry(() =>
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static async Task MoveReplaceAsync(string temp, string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < Attempts)
            {
                await Task.Delay(Delay(attempt), cancellationToken);
            }
        }
    }

    public static void MoveReplace(string temp, string path) => Retry(() =>
    {
        File.Move(temp, path, overwrite: true);
        return true;
    });

    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < Attempts)
            {
                Thread.Sleep(Delay(attempt));
            }
        }
    }

    // A missing file or folder will not appear by waiting; a sharing violation or a momentary access denial during a replace will go away.
    private static bool IsTransient(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is IOException and not FileNotFoundException and not DirectoryNotFoundException);

    private static TimeSpan Delay(int attempt) => TimeSpan.FromMilliseconds(Math.Min(MaxDelayMs, FirstDelayMs * (1 << Math.Min(attempt, 5))));
}
