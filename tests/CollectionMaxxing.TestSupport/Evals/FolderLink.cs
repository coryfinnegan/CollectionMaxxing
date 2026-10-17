using System.Diagnostics;

namespace CollectionMaxxing.TestSupport.Evals;

/// <summary>
/// Makes a folder that is a link to another folder, on any OS and without admin rights: a junction on Windows (a symbolic link there
/// needs a privilege), a symbolic link elsewhere. Both are what a "link" means to the golden-set code.
/// </summary>
public static class FolderLink
{
    public static void Create(string linkPath, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{linkPath}' -Target '{target}' | Out-Null");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start powershell.exe.");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Could not create the junction: " + error);
            }
        }
        else
        {
            Directory.CreateSymbolicLink(linkPath, target);
        }

        if (!new DirectoryInfo(linkPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The folder link was not created.");
        }
    }
}
