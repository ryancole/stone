using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BehaveDiff.Capture;

/// <summary>
/// Records one http-response observation per request handled by ASP.NET Core hosting.
/// The response body is captured by teeing HttpResponse.Body at request start.
/// </summary>
sealed class HttpCapture(ObservationWriter writer) : IObserver<KeyValuePair<string, object?>>
{
    public const string ListenerName = "Microsoft.AspNetCore";
    const string ActivityName = "Microsoft.AspNetCore.Hosting.HttpRequestIn";
    const string StartEvent = ActivityName + ".Start";
    const string StopEvent = ActivityName + ".Stop";
    const int MaxBodyBytes = 1024 * 1024;

    public static bool IsEnabled(string name) => name.StartsWith(ActivityName, StringComparison.Ordinal);

    sealed class RequestState
    {
        public required TeeStream Tee;
        public required long StartTicks;
        public required DateTimeOffset StartedAt;
        public required TestAttribution.Result Attribution;
    }

    readonly ConditionalWeakTable<object, RequestState> _requests = new();
    readonly ConditionalWeakTable<Activity, object> _contextByActivity = new();

    /// <summary>
    /// The request whose pipeline the caller is running in, found through Activity.Current
    /// (hosting starts an Activity per request and it flows with the async context).
    /// </summary>
    public (string Trigger, TestAttribution.Result Attribution)? CurrentRequest()
    {
        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
        {
            if (_contextByActivity.TryGetValue(activity, out var ctx) && _requests.TryGetValue(ctx, out var state))
                return (Trigger(ctx), state.Attribution);
        }
        return null;
    }

    public void OnNext(KeyValuePair<string, object?> evt)
    {
        try
        {
            switch (evt.Key)
            {
                case StartEvent when evt.Value is { } ctx: OnStart(ctx); break;
                case StopEvent when evt.Value is { } ctx: OnStop(ctx); break;
            }
        }
        catch (Exception ex)
        {
            writer.Error(evt.Key, ex);
        }
    }

    void OnStart(object httpContext)
    {
        var response = Reflect.Get(httpContext, "Response")!;
        var original = (Stream)Reflect.Get(response, "Body")!;
        var tee = new TeeStream(original, MaxBodyBytes);
        Reflect.Set(response, "Body", tee);
        _requests.AddOrUpdate(httpContext, new RequestState
        {
            Tee = tee,
            StartTicks = Stopwatch.GetTimestamp(),
            StartedAt = DateTimeOffset.UtcNow,
            Attribution = TestAttribution.FromStack(),
        });
        // Hosting has started the request Activity before raising .Start.
        if (Activity.Current is { OperationName: ActivityName } activity)
            _contextByActivity.AddOrUpdate(activity, httpContext);
    }

    static string Trigger(object httpContext)
    {
        var request = Reflect.Get(httpContext, "Request")!;
        var method = (string)Reflect.Get(request, "Method")!;
        return $"{method} {RouteTemplate(httpContext) ?? Reflect.Get(request, "Path")?.ToString()}";
    }

    void OnStop(object httpContext)
    {
        if (!_requests.TryGetValue(httpContext, out var state)) return;
        _requests.Remove(httpContext);

        var request = Reflect.Get(httpContext, "Request")!;
        var response = Reflect.Get(httpContext, "Response")!;
        var method = (string)Reflect.Get(request, "Method")!;
        var path = Reflect.Get(request, "Path")?.ToString() ?? "";
        var query = Reflect.Get(request, "QueryString")?.ToString() ?? "";
        var route = RouteTemplate(httpContext);
        var status = (int)Reflect.Get(response, "StatusCode")!;
        var contentType = Reflect.Get(response, "ContentType") as string;

        var headers = new JsonObject();
        foreach (var kv in Reflect.Enumerate(Reflect.Get(response, "Headers")))
            headers[(string)Reflect.Get(kv, "Key")!] = Reflect.Get(kv, "Value")?.ToString();

        var data = new JsonObject
        {
            ["request"] = new JsonObject { ["method"] = method, ["path"] = path, ["query"] = query, ["route"] = route },
            ["status"] = status,
            ["headers"] = headers,
            ["body"] = Body(state.Tee, contentType),
            ["bodyTruncated"] = state.Tee.Truncated ? true : null,
        };

        var trigger = Trigger(httpContext);
        var elapsed = Stopwatch.GetElapsedTime(state.StartTicks).TotalMilliseconds;
        writer.Write("http-response", trigger, data, elapsed, state.Attribution, state.StartedAt);
    }

    /// <summary>"/workflows/{id}" from the matched RouteEndpoint, or null when no endpoint matched.</summary>
    static string? RouteTemplate(object httpContext)
    {
        foreach (var kv in Reflect.Enumerate(Reflect.Get(httpContext, "Features")))
        {
            if (Reflect.Get(kv, "Key") is not Type { Name: "IEndpointFeature" }) continue;
            var endpoint = Reflect.Get(Reflect.Get(kv, "Value"), "Endpoint");
            var raw = Reflect.Get(Reflect.Get(endpoint, "RoutePattern"), "RawText") as string;
            if (raw is null) return null;
            return raw.StartsWith('/') ? raw : "/" + raw;
        }
        return null;
    }

    static JsonNode? Body(TeeStream tee, string? contentType)
    {
        var bytes = tee.Captured;
        if (bytes.Length == 0) return null;
        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true && !tee.Truncated)
        {
            try { return JsonNode.Parse(bytes); }
            catch (JsonException) { }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }
}

/// <summary>Write-through stream that keeps a copy of the first N bytes written.</summary>
sealed class TeeStream(Stream inner, int limit) : Stream
{
    readonly MemoryStream _copy = new();

    public bool Truncated { get; private set; }
    public ReadOnlySpan<byte> Captured => _copy.GetBuffer().AsSpan(0, (int)_copy.Length);

    void Keep(ReadOnlySpan<byte> data)
    {
        var room = limit - (int)_copy.Length;
        if (data.Length > room) { Truncated = true; data = data[..Math.Max(room, 0)]; }
        _copy.Write(data);
    }

    public override void Write(byte[] buffer, int offset, int count) { Keep(buffer.AsSpan(offset, count)); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Keep(buffer); inner.Write(buffer); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { Keep(buffer.AsSpan(offset, count)); return inner.WriteAsync(buffer, offset, count, ct); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Keep(buffer.Span); return inner.WriteAsync(buffer, ct); }
    public override void WriteByte(byte value) { Keep([value]); inner.WriteByte(value); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
