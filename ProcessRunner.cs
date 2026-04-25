// Process runner port: absorbs child-process execution, OS shell wrapping,
// output tail buffering, and the non-zero-exit → failed-step convention.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Spectre.Console;

internal interface IProcessRunner
{
    ProcRunResult Run(ProcSpec spec, RunContext ctx);
}

internal readonly record struct ProcSpec(
    string FileName,
    string Arguments,
    string DisplayShell,
    string DisplayCommandLine,
    int TailLines = 50)
{
    public static ProcSpec Exec(string file, string args, int tail = 50)
        => new(file, args, "exec",
               string.IsNullOrEmpty(args) ? file : $"{file} {args}", tail);

    public static ProcSpec Shell(string commandLine, int tail = 50)
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new("cmd.exe", $"/c {commandLine}", "cmd", commandLine, tail)
            : new("bash", $"-c \"{commandLine}\"", "bash", commandLine, tail);
}

internal sealed record ProcRunResult(
    int ExitCode,
    string[] StderrTail,
    string[] StdoutTail,
    string DisplayShell,
    string DisplayCommandLine)
{
    public bool Ok => ExitCode == 0;
}

internal sealed class RealProcessRunner : IProcessRunner
{
    public ProcRunResult Run(ProcSpec spec, RunContext ctx)
    {
        var psi = new ProcessStartInfo(spec.FileName, spec.Arguments)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = ctx.JsonMode,
        };

        using var p = new Process { StartInfo = psi };
        var errBuffer = new Queue<string>();
        var outBuffer = new Queue<string>();
        var bufferLock = new object();

        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (bufferLock)
            {
                if (!ctx.JsonMode) Console.Error.WriteLine(e.Data);
                errBuffer.Enqueue(e.Data);
                while (errBuffer.Count > spec.TailLines) errBuffer.Dequeue();
            }
        };
        if (ctx.JsonMode)
        {
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (bufferLock)
                {
                    outBuffer.Enqueue(e.Data);
                    while (outBuffer.Count > spec.TailLines) outBuffer.Dequeue();
                }
            };
        }

        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            if (!ctx.JsonMode) AnsiConsole.WriteException(ex);
            return new ProcRunResult(-1, new[] { ex.Message }, Array.Empty<string>(),
                                     spec.DisplayShell, spec.DisplayCommandLine);
        }

        p.BeginErrorReadLine();
        if (ctx.JsonMode) p.BeginOutputReadLine();
        p.WaitForExit();

        lock (bufferLock)
            return new ProcRunResult(p.ExitCode, errBuffer.ToArray(), outBuffer.ToArray(),
                                     spec.DisplayShell, spec.DisplayCommandLine);
    }
}

internal static class ProcRunnerExtensions
{
    public static bool RunOrFail(
        this IProcessRunner runner,
        ProcSpec spec,
        StepResult step,
        RunContext ctx,
        string errorCode,
        string? errorMessage,
        out ProcRunResult result)
    {
        result = runner.Run(spec, ctx);
        if (result.Ok) return true;

        step.Status = "failed";
        step.ExitCode = 1;
        step.Error = new StepError
        {
            Code = errorCode,
            Message = errorMessage ?? $"{spec.DisplayCommandLine} exited with code {result.ExitCode}",
            Detail = new Dictionary<string, object?>
            {
                ["exitCode"] = result.ExitCode,
                ["stderrTail"] = result.StderrTail,
                ["stdoutTail"] = result.StdoutTail,
            },
        };
        return false;
    }
}
