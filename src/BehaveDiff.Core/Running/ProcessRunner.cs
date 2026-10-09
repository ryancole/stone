using System.Diagnostics;
using System.Text;

namespace BehaveDiff.Core.Running;

public sealed record ProcessResult(int ExitCode, string Output);

static class ProcessRunner
{
    /// <summary>
    /// Runs a process to completion, capturing stdout+stderr (also appended to <paramref name="logFile"/>).
    /// Cancellation kills the whole process tree.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? env = null,
        string? logFile = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Lingering MSBuild nodes lock output DLLs between the two trees' builds.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        if (env is not null)
            foreach (var (k, v) in env)
            {
                if (v is null) psi.Environment.Remove(k);
                else psi.Environment[k] = v;
            }

        var output = new StringBuilder();
        var gate = new object();
        StreamWriter? log = null;
        if (logFile is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            log = new StreamWriter(logFile, append: true, new UTF8Encoding(false));
            log.WriteLine($"> {fileName} {string.Join(' ', psi.ArgumentList)}");
        }

        void OnLine(string? line)
        {
            if (line is null) return;
            lock (gate)
            {
                output.AppendLine(line);
                log?.WriteLine(line);
            }
        }

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Could not start {fileName}");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            process.WaitForExit(); // flush async output handlers
            return new ProcessResult(process.ExitCode, output.ToString());
        }
        finally
        {
            lock (gate) log?.Dispose();
        }
    }
}
