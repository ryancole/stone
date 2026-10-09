using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

namespace BehaveDiff.Capture;

/// <summary>
/// In-process attempt to find the running test: walk the stack for a method marked with
/// an attribute deriving from MSTest's TestMethodAttribute. Works when the event fires on
/// the test's own call chain; server-side work under TestServer usually runs on a pool
/// thread and comes back null. Timestamp correlation with the TRX report covers the rest.
/// </summary>
static class TestAttribution
{
    public readonly record struct Result(string? Test, string? Source);

    static readonly ConcurrentDictionary<MethodBase, string?> Cache = new();

    public static Result FromStack()
    {
        try
        {
            foreach (var frame in new StackTrace(skipFrames: 1, fNeedFileInfo: false).GetFrames())
            {
                var method = frame.GetMethod();
                if (method is null) continue;
                var name = Cache.GetOrAdd(method, Resolve);
                if (name is not null) return new(name, "stack");
            }
        }
        catch { }
        return new(null, null);
    }

    static string? Resolve(MethodBase method)
    {
        // Async test methods appear as <Name>d__N.MoveNext on a nested state machine type.
        var type = method.DeclaringType;
        var methodName = method.Name;
        if (methodName == "MoveNext" && type?.DeclaringType is { } outer && type.Name.StartsWith('<'))
        {
            methodName = type.Name[1..type.Name.IndexOf('>')];
            type = outer;
        }
        if (type is null) return null;

        var candidate = methodName == method.Name
            ? method
            : type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == methodName);
        if (candidate is null || !IsTestMethod(candidate)) return null;
        return $"{type.Name}.{methodName}";
    }

    static bool IsTestMethod(MethodBase method)
    {
        foreach (var data in method.CustomAttributes)
        {
            for (var t = data.AttributeType; t is not null; t = t.BaseType)
                if (t.FullName == "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute")
                    return true;
        }
        return false;
    }
}
