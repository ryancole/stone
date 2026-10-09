using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BehaveDiff.Capture;

/// <summary>
/// Records db-write observations from EF Core's SaveChanges diagnostic events.
/// Pending changes are snapshotted at SaveChangesStarting and emitted at Completed/Failed,
/// so store-generated keys are filled in and failed saves are marked as such.
/// Works regardless of how the DbContext was registered (AddDbContext, custom factories, ...).
/// </summary>
sealed class EfCapture(ObservationWriter writer, HttpCapture http) : IObserver<KeyValuePair<string, object?>>
{
    public const string ListenerName = "Microsoft.EntityFrameworkCore";
    const string Prefix = "Microsoft.EntityFrameworkCore.Update.SaveChanges";
    const string Starting = Prefix + "Starting";
    const string Completed = Prefix + "Completed";
    const string Failed = Prefix + "Failed";
    const string Canceled = Prefix + "Canceled";

    public static bool IsEnabled(string name) => name.StartsWith(Prefix, StringComparison.Ordinal);

    sealed record PendingWrite(object Entry, string Entity, string State, JsonObject Values, string[] KeyNames);

    sealed class PendingSave
    {
        public required List<PendingWrite> Writes;
        public required long StartTicks;
        public required DateTimeOffset StartedAt;
        public required string Trigger;
        public required TestAttribution.Result Attribution;
    }

    readonly ConditionalWeakTable<object, PendingSave> _pending = new();

    public void OnNext(KeyValuePair<string, object?> evt)
    {
        try
        {
            var context = Reflect.Get(evt.Value, "Context");
            if (context is null) return;
            switch (evt.Key)
            {
                case Starting: OnStarting(context); break;
                case Completed: OnFinished(context, "saved"); break;
                case Failed: OnFinished(context, "failed"); break;
                case Canceled: OnFinished(context, "canceled"); break;
            }
        }
        catch (Exception ex)
        {
            writer.Error(evt.Key, ex);
        }
    }

    void OnStarting(object context)
    {
        // Entries() runs DetectChanges when auto-detect is on, same as SaveChanges is about to.
        var changeTracker = Reflect.Get(context, "ChangeTracker");
        var writes = new List<PendingWrite>();
        foreach (var entry in Reflect.Enumerate(Reflect.Call(changeTracker, "Entries")))
        {
            var state = Reflect.Get(entry, "State")!.ToString()!;
            if (state is not ("Added" or "Modified" or "Deleted")) continue;
            writes.Add(Snapshot(entry, state));
        }

        var (trigger, attribution) = http.CurrentRequest() ?? ("(test code)", TestAttribution.FromStack());
        _pending.AddOrUpdate(context, new PendingSave
        {
            Writes = writes,
            StartTicks = Stopwatch.GetTimestamp(),
            StartedAt = DateTimeOffset.UtcNow,
            Trigger = trigger,
            Attribution = attribution,
        });
    }

    void OnFinished(object context, string outcome)
    {
        if (!_pending.TryGetValue(context, out var save)) return;
        _pending.Remove(context);

        var contextName = context.GetType().Name;
        var elapsed = Stopwatch.GetElapsedTime(save.StartTicks).TotalMilliseconds;
        foreach (var write in save.Writes)
        {
            // Store-generated keys (identity, sequences) are temporary until the save completes.
            if (outcome == "saved" && write.State == "Added")
                foreach (var key in write.KeyNames)
                    write.Values[key] = ToJson(Reflect.Get(Reflect.Call(write.Entry, "Property", key), "CurrentValue"));

            var data = new JsonObject
            {
                ["context"] = contextName,
                ["entity"] = write.Entity,
                ["state"] = write.State,
                ["keys"] = new JsonArray(write.KeyNames.Select(k => (JsonNode?)k).ToArray()),
                ["values"] = write.Values,
                ["outcome"] = outcome == "saved" ? null : outcome,
            };
            writer.Write("db-write", save.Trigger, data, elapsed, save.Attribution, save.StartedAt);
        }
    }

    static PendingWrite Snapshot(object entry, string state)
    {
        var metadata = Reflect.Get(entry, "Metadata");
        var entity = (Reflect.Get(metadata, "ClrType") as Type)?.Name ?? Reflect.Get(metadata, "Name")?.ToString() ?? "?";
        var keyNames = Reflect.Enumerate(Reflect.Get(Reflect.Call(metadata, "FindPrimaryKey"), "Properties"))
            .Select(p => (string)Reflect.Get(p, "Name")!)
            .ToArray();

        var values = new JsonObject();
        foreach (var prop in Reflect.Enumerate(Reflect.Get(entry, "Properties")))
        {
            var name = (string)Reflect.Get(Reflect.Get(prop, "Metadata"), "Name")!;
            var isKey = keyNames.Contains(name);
            switch (state)
            {
                case "Added":
                    values[name] = ToJson(Reflect.Get(prop, "CurrentValue"));
                    break;
                case "Deleted":
                    values[name] = ToJson(Reflect.Get(prop, "OriginalValue"));
                    break;
                case "Modified" when isKey:
                    values[name] = ToJson(Reflect.Get(prop, "CurrentValue"));
                    break;
                case "Modified" when Reflect.Get(prop, "IsModified") is true:
                    values[name] = new JsonObject
                    {
                        ["original"] = ToJson(Reflect.Get(prop, "OriginalValue")),
                        ["current"] = ToJson(Reflect.Get(prop, "CurrentValue")),
                    };
                    break;
            }
        }
        return new PendingWrite(entry, entity, state, values, keyNames);
    }

    static JsonNode? ToJson(object? value)
    {
        switch (value)
        {
            case null: return null;
            case Enum e: return e.ToString();
            case byte[] b: return Convert.ToBase64String(b);
        }
        try { return JsonSerializer.SerializeToNode(value, value.GetType()); }
        catch { return value.ToString(); }
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }
}
