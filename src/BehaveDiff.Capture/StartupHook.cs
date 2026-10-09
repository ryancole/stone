using System.Diagnostics;
using System.Reflection;
using BehaveDiff.Capture;

/// <summary>
/// Entry point for DOTNET_STARTUP_HOOKS. Must be named StartupHook, live in no namespace,
/// and expose a static Initialize().
/// </summary>
public static class StartupHook
{
    public const string CaptureDirVar = "BEHAVEDIFF_CAPTURE_DIR";

    // Processes that inherit the env var but are never the app under test.
    static readonly HashSet<string> IgnoredEntryAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "MSBuild", "testhost", "vstest.console",
    };

    public static void Initialize()
    {
        var dir = Environment.GetEnvironmentVariable(CaptureDirVar);
        if (string.IsNullOrWhiteSpace(dir))
            return;

        try
        {
            var entry = Assembly.GetEntryAssembly()?.GetName().Name ?? "(unknown)";
            Directory.CreateDirectory(dir);
            var writer = new ObservationWriter(dir, Environment.ProcessId);
            writer.Log($"hook loaded: entry={entry} pid={Environment.ProcessId} runtime={Environment.Version} cmd={Environment.CommandLine}");

            if (IgnoredEntryAssemblies.Contains(entry))
            {
                writer.Log("entry assembly is on the ignore list; capture disabled in this process");
                return;
            }

            var http = new HttpCapture(writer);
            var ef = new EfCapture(writer, http);
            DiagnosticListener.AllListeners.Subscribe(new ListenerObserver(listener =>
            {
                switch (listener.Name)
                {
                    case HttpCapture.ListenerName:
                        listener.Subscribe(http, HttpCapture.IsEnabled);
                        writer.Log($"subscribed to {listener.Name}");
                        break;
                    case EfCapture.ListenerName:
                        listener.Subscribe(ef, EfCapture.IsEnabled);
                        writer.Log($"subscribed to {listener.Name}");
                        break;
                }
            }));
        }
        catch (Exception ex)
        {
            // A startup hook that throws kills the host process. Never let that happen.
            try { File.AppendAllText(Path.Combine(dir, $"hook-error-{Environment.ProcessId}.log"), ex + Environment.NewLine); }
            catch { }
        }
    }

    sealed class ListenerObserver(Action<DiagnosticListener> onNext) : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener value) => onNext(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
