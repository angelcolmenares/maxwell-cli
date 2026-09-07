using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Maxwell.Agents.Tools;

/// <summary>
/// "bash" - runs a shell command for file/system operations (ls, grep, find,
/// git, test runners, ...) that don't need the precision of the dedicated
/// read/edit/write tools. Commands run through /bin/bash on Linux/macOS and
/// cmd.exe on Windows, rooted at the project's working directory unless the
/// model passes its own workingDirectory.
/// </summary>
public sealed class BashTool(string rootDirectory)
{
    private const int DefaultTimeoutSeconds = 60;
    private const int MaxTimeoutSeconds = 600;
    private const int MaxOutputChars = 30_000;

    [Description(
        "Execute a shell command and return its stdout, stderr, and exit code. Use this for file " +
        "operations like ls, grep, find, git, or running tests/build tools - not for editing files " +
        "(use 'edit' or 'write' instead). Commands time out after 60 seconds by default.")]
    public async Task<string> Run(
        [Description("The shell command to execute.")]
        string command,
        [Description("Directory to run the command in, absolute or relative to the project's working directory. Defaults to the project's working directory.")]
        string? workingDirectory = null,
        [Description("Maximum seconds to let the command run before it is killed. Defaults to 60, capped at 600.")]
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "Error: command must not be empty.";
        }

        string cwd;
        try
        {
            cwd = string.IsNullOrWhiteSpace(workingDirectory)
                ? rootDirectory
                : ToolPaths.Resolve(rootDirectory, workingDirectory);
        }
        catch (Exception ex)
        {
            return $"Error: invalid workingDirectory '{workingDirectory}' ({ex.Message}).";
        }

        if (!Directory.Exists(cwd))
        {
            return $"Error: working directory not found: {ToolPaths.Display(rootDirectory, cwd)}";
        }

        var timeout = Math.Clamp(timeoutSeconds ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds);

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName =  @"C:\Archivos de programa\Git\bin\bash.exe";
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.FileName = "/bin/bash";
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return $"Error: could not start command: {ex.Message}";
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        bool timedOut;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            timedOut = false;
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            TryKill(process);

            if (!timedOut)
            {
                throw; // caller's own cancellationToken fired; propagate rather than report a false timeout.
            }
        }

        var result = new StringBuilder();
        result.AppendLine($"$ {command}");
        result.AppendLine($"(cwd: {ToolPaths.Display(rootDirectory, cwd)})");

        if (timedOut)
        {
            result.AppendLine($"Error: command timed out after {timeout}s and was killed.");
        }
        else
        {
            result.AppendLine($"exit code: {process.ExitCode}");
        }

        AppendStream(result, "stdout", stdout.ToString());
        AppendStream(result, "stderr", stderr.ToString());

        return Truncate(result.ToString());
    }

    private static void AppendStream(StringBuilder result, string label, string content)
    {
        content = content.TrimEnd('\n', '\r');
        if (content.Length == 0)
        {
            return;
        }

        result.AppendLine($"--- {label} ---");
        result.AppendLine(content);
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaxOutputChars)
        {
            return text;
        }

        var omitted = text.Length - MaxOutputChars;
        return string.Concat(text.AsSpan(0, MaxOutputChars), $"\n... [{omitted} more characters truncated] ...");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort: the process may have exited between the check and the kill.
        }
    }
}
