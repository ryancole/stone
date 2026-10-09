using System.Collections;
using System.Reflection;

namespace BehaveDiff.Capture;

/// <summary>
/// Late-bound member access. Looks on the runtime type first, then its interfaces,
/// so explicit interface implementations (common in ASP.NET Core / EF Core) resolve.
/// </summary>
static class Reflect
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;

    public static object? Get(object? target, string name)
    {
        if (target is null) return null;
        var prop = FindProperty(target.GetType(), name);
        return prop?.GetValue(target);
    }

    public static void Set(object target, string name, object? value)
    {
        var prop = FindProperty(target.GetType(), name)
            ?? throw new MissingMemberException(target.GetType().FullName, name);
        prop.SetValue(target, value);
    }

    public static object? Call(object? target, string name, params object?[] args)
    {
        if (target is null) return null;
        var method = FindMethod(target.GetType(), name, args)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        return method.Invoke(target, args);
    }

    public static IEnumerable<object> Enumerate(object? target) =>
        target is IEnumerable e ? e.Cast<object>() : [];

    static PropertyInfo? FindProperty(Type type, string name)
    {
        // Most-derived first; a derived type may hide a base property with `new`.
        for (var t = type; t is not null; t = t.BaseType)
        {
            var p = t.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
            if (p is not null && p.GetIndexParameters().Length == 0) return p;
        }
        return type.GetInterfaces()
            .Select(i => i.GetProperty(name, Flags))
            .FirstOrDefault(p => p is not null && p.GetIndexParameters().Length == 0);
    }

    static MethodInfo? FindMethod(Type type, string name, object?[] args) =>
        type.GetMethods(Flags).FirstOrDefault(m => Accepts(m, name, args))
        ?? type.GetInterfaces().SelectMany(i => i.GetMethods(Flags)).FirstOrDefault(m => Accepts(m, name, args));

    // Overload resolution by argument runtime types, e.g. EntityEntry.Property(string) vs Property(IProperty).
    static bool Accepts(MethodInfo m, string name, object?[] args)
    {
        if (m.Name != name || m.IsGenericMethodDefinition) return false;
        var ps = m.GetParameters();
        if (ps.Length != args.Length) return false;
        for (var i = 0; i < ps.Length; i++)
        {
            if (args[i] is null ? ps[i].ParameterType.IsValueType : !ps[i].ParameterType.IsInstanceOfType(args[i]))
                return false;
        }
        return true;
    }
}
