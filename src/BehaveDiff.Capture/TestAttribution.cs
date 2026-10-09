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
    static PropertyInfo? _testContextCurrent;

    /// <summary>MSTest 4's TestContext.Current (AsyncLocal), else a stack scan.</summary>
    public static Result Find()
    {
        var fromContext = FromTestContext();
        return fromContext.Source is not null ? fromContext : FromStack();
    }

    /// <summary>
    /// For work done directly by test code (not inside a request). MSTest is present but no test
    /// is current only while fixture code runs (AssemblyInitialize / ClassInitialize).
    /// </summary>
    public static Result ForTestCode()
    {
        var found = Find();
        return found.Test is null && _testContextCurrent is not null ? new("(setup)", "mstest-context") : found;
    }

    /// <summary>
    /// TestContext.Current flows with the async context MSTest runs test code on, but not onto
    /// TestServer's request threads.
    /// </summary>
    static Result FromTestContext()
    {
        try
        {
            _testContextCurrent ??= AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Microsoft.VisualStudio.TestTools.UnitTesting.TestContext", throwOnError: false))
                .FirstOrDefault(t => t is not null)
                ?.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var context = _testContextCurrent?.GetValue(null);
            if (context is null) return new(null, null);
            var test = Reflect.Get(context, "TestName") as string;
            var cls = (Reflect.Get(context, "FullyQualifiedTestClassName") as string)?.Split('.')[^1];
            return test is null
                ? new("(setup)", "mstest-context")
                : new(cls is null ? test : $"{cls}.{test}", "mstest-context");
        }
        catch { return new(null, null); }
    }

    static Result FromStack()
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
