using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BehaveDiff.Capture;

/// <summary>Appends observations as JSONL; one file per process. Thread-safe.</summary>
sealed class ObservationWriter
{
    readonly object _gate = new();
    readonly string _observationsPath;
    StreamWriter? _observations; // opened on first observation; MTP's controller process never gets one
    readonly StreamWriter _log;
    long _seq;

    static readonly JsonSerializerOptions LineOptions = new() { WriteIndented = false };

    public ObservationWriter(string dir, int pid)
    {
        _observationsPath = Path.Combine(dir, $"observations-{pid}.jsonl");
        _log = Open(Path.Combine(dir, $"hook-{pid}.log"));
    }

    static StreamWriter Open(string path) =>
        new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };

    public void Log(string message)
    {
        lock (_gate)
            _log.WriteLine($"{DateTimeOffset.UtcNow:O} {message}");
    }

    /// <param name="kind">Open-ended observation kind, e.g. http-response, db-write.</param>
    public void Write(string kind, string trigger, JsonNode data, double? durationMs, TestAttribution.Result attribution, DateTimeOffset timestamp)
    {
        var obj = new JsonObject
        {
            ["seq"] = Interlocked.Increment(ref _seq),
            ["test"] = attribution.Test,
            ["trigger"] = trigger,
            ["kind"] = kind,
            ["data"] = data,
            ["durationMs"] = durationMs is { } d ? Math.Round(d, 3) : null,
            // Spike diagnostics: used to evaluate attribution strategies offline.
            ["ts"] = timestamp.ToString("O"),
            ["threadId"] = Environment.CurrentManagedThreadId,
            ["attribution"] = attribution.Source,
        };
        var line = obj.ToJsonString(LineOptions);
        lock (_gate)
            (_observations ??= Open(_observationsPath)).WriteLine(line);
    }

    public void Error(string where, Exception ex) => Log($"ERROR in {where}: {ex}");
}
