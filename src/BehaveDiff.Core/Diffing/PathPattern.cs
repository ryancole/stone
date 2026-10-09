using System.Text.RegularExpressions;

namespace BehaveDiff.Core.Diffing;

/// <summary>
/// Path patterns over observation data: "$.body.items[*].etag", "$.headers.X-Foo".
/// "[*]" matches any index, "*" any single property name. A pattern also matches everything below it.
/// "$" alone matches the whole observation (used for one-sided noise).
/// </summary>
static class PathPattern
{
    static readonly Dictionary<string, Regex> Cache = new();

    public static bool Matches(string pattern, string? path)
    {
        if (pattern == "$") return true;
        if (path is null) return false;
        Regex regex;
        lock (Cache)
        {
            if (!Cache.TryGetValue(pattern, out regex!))
            {
                var p = Regex.Escape(pattern)
                    .Replace(@"\[\*]", @"\[\d+]")
                    .Replace(@"\*", @"[^.\[]+");
                regex = Cache[pattern] = new Regex("^" + p + @"(?:$|[.\[])", RegexOptions.CultureInvariant);
            }
        }
        return regex.IsMatch(path);
    }

    /// <summary>"$.body.items[3].etag" -> "$.body.items[*].etag".</summary>
    public static string Generalize(string path) => Regex.Replace(path, @"\[\d+]", "[*]");
}
