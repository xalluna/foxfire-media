using System.ComponentModel;
using System.Diagnostics;

namespace Foxfire.Server.Media;

public sealed record ProcessResult(int ExitCode, string StandardOutput, IReadOnlyList<string> StandardErrorTail);

/// <summary>Runs a tool with an argument list (never through a shell) and kills the whole tree on cancellation.</summary>
public static class ProcessRunner
{
    private const int StderrTailLines = 30;

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onStdoutLine,
        CancellationToken ct,
        bool captureStdout = false)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        var stdout = new System.Text.StringBuilder();
        var stderr = new Queue<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            if (captureStdout)
            {
                lock (stdout)
                {
                    stdout.AppendLine(e.Data);
                }
            }

            onStdoutLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stderr)
            {
                stderr.Enqueue(e.Data);
                if (stderr.Count > StderrTailLines)
                {
                    stderr.Dequeue();
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start '{fileName}'. Is it installed and on PATH (or configured under Media)?", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        // Flush async readers.
        process.WaitForExit();

        lock (stderr)
        {
            return new ProcessResult(process.ExitCode, stdout.ToString(), [.. stderr]);
        }
    }
}
