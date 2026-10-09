using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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

    sealed record PendingWrite(object Entry, string Entity, string State, JsonObject Values, JsonObject ProviderValues, string[] KeyNames, JsonObject ForeignKeys);

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

        var (trigger, attribution) = http.CurrentRequest() ?? ("(test code)", TestAttribution.ForTestCode());
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
            // Store-generated keys (identity, sequences), and FKs pointing at rows inserted in the
            // same save, hold temporary values until the save completes.
            if (outcome == "saved" && write.State == "Added")
                foreach (var key in write.KeyNames.Concat(write.ForeignKeys.Select(fk => fk.Key)).Distinct())
                    write.Values[key] = ToJson(Reflect.Get(Reflect.Call(write.Entry, "Property", key), "CurrentValue"));

            var data = new JsonObject
            {
                ["context"] = contextName,
                ["entity"] = write.Entity,
                ["state"] = write.State,
                ["keys"] = new JsonArray(write.KeyNames.Select(k => (JsonNode?)k).ToArray()),
                ["values"] = write.Values,
                // Single-column FKs: column -> principal entity, so ids can be compared by insert order.
                ["foreignKeys"] = write.ForeignKeys.Count > 0 ? write.ForeignKeys : null,
                // Only columns whose stored form differs from the CLR value (value converters).
                ["providerValues"] = write.ProviderValues.Count > 0 ? write.ProviderValues : null,
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
        var providerValues = new JsonObject();
        foreach (var prop in Reflect.Enumerate(Reflect.Get(entry, "Properties")))
        {
            var propMeta = Reflect.Get(prop, "Metadata")!;
            var name = (string)Reflect.Get(propMeta, "Name")!;
            var isKey = keyNames.Contains(name);
            var converter = ConverterOf(propMeta);
            switch (state)
            {
                case "Added":
                    Record(name, Reflect.Get(prop, "CurrentValue"));
                    break;
                case "Deleted":
                    Record(name, Reflect.Get(prop, "OriginalValue"));
                    break;
                case "Modified" when isKey:
                    Record(name, Reflect.Get(prop, "CurrentValue"));
                    break;
                case "Modified" when Reflect.Get(prop, "IsModified") is true:
                    var original = Reflect.Get(prop, "OriginalValue");
                    var current = Reflect.Get(prop, "CurrentValue");
                    values[name] = new JsonObject { ["original"] = ToJson(original), ["current"] = ToJson(current) };
                    if (ProviderDiffers(converter, original, out var origProvider) | ProviderDiffers(converter, current, out var curProvider))
                        providerValues[name] = new JsonObject { ["original"] = origProvider, ["current"] = curProvider };
                    break;
            }

            void Record(string column, object? value)
            {
                values[column] = ToJson(value);
                if (ProviderDiffers(converter, value, out var provider))
                    providerValues[column] = provider;
            }
        }
        return new PendingWrite(entry, entity, state, values, providerValues, keyNames, ForeignKeysOf(metadata));
    }

    static JsonObject ForeignKeysOf(object? entityType)
    {
        var result = new JsonObject();
        try
        {
            foreach (var fk in Reflect.Enumerate(Reflect.Call(entityType, "GetForeignKeys")))
            {
                var props = Reflect.Enumerate(Reflect.Get(fk, "Properties")).ToList();
                if (props.Count != 1) continue;
                var principal = Reflect.Get(Reflect.Get(fk, "PrincipalEntityType"), "ClrType") as Type;
                if (principal is not null)
                    result[(string)Reflect.Get(props[0], "Name")!] = principal.Name;
            }
        }
        catch { }
        return result;
    }

    /// <summary>The value converter EF applies for this column, explicit or by convention; null when none.</summary>
    static Func<object?, object?>? ConverterOf(object propertyMetadata)
    {
        try
        {
            var mapping = Reflect.Call(propertyMetadata, "GetTypeMapping") ?? Reflect.Call(propertyMetadata, "FindTypeMapping");
            return Reflect.Get(Reflect.Get(mapping, "Converter"), "ConvertToProvider") as Func<object?, object?>;
        }
        catch { return null; }
    }

    /// <summary>
    /// The value as sent to the database, when a converter changes it in a way that matters.
    /// Enum-to-number conventions don't count: the CLR value already says the same thing.
    /// </summary>
    static bool ProviderDiffers(Func<object?, object?>? converter, object? value, out JsonNode? provider)
    {
        provider = null;
        if (converter is null || value is null) return false;
        try
        {
            provider = ToJson(converter(value), NumericEnums);
            return !JsonNode.DeepEquals(provider, ToJson(value, NumericEnums));
        }
        catch { return false; }
    }

    static readonly JsonSerializerOptions NamedEnums = new() { Converters = { new JsonStringEnumConverter() } };
    static readonly JsonSerializerOptions NumericEnums = new();

    /// <summary>JSON for a column value. Enums as names (including inside collections) by default.</summary>
    static JsonNode? ToJson(object? value, JsonSerializerOptions? options = null)
    {
        switch (value)
        {
            case null: return null;
            case byte[] b: return Convert.ToBase64String(b);
        }
        try { return JsonSerializer.SerializeToNode(value, value.GetType(), options ?? NamedEnums); }
        catch { return value.ToString(); }
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }
}
