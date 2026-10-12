using System.Diagnostics;
using System.Reflection;

namespace BetterNotepad.Tabular.Sample;

/// <summary>Marks a static, parameterless method as a test case.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class CaseAttribute : Attribute;

internal sealed class CheckFailed(string message) : Exception(message);

/// <summary>Tiny assertion helpers (no test framework dependency, so the harness also runs from `dotnet run`).</summary>
internal static class Check
{
    public static void True(bool condition, string message = "expected true")
    {
        if (!condition) throw new CheckFailed(message);
    }

    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new CheckFailed($"{what ?? "values"}: expected <{Show(expected)}> but got <{Show(actual)}>");
    }

    public static void SeqEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? what = null)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (e.Count != a.Count || !e.SequenceEqual(a))
            throw new CheckFailed($"{what ?? "sequence"}: expected [{string.Join(" | ", e.Select(x => Show(x)))}] but got [{string.Join(" | ", a.Select(x => Show(x)))}]");
    }

    public static void Throws<TException>(Action action, string? what = null) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new CheckFailed($"{what ?? "call"}: expected {typeof(TException).Name}");
    }

    public static string Show<T>(T value) => value switch
    {
        null => "null",
        string s => "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\uFEFF", "\\uFEFF") + "\"",
        char c => "'" + (c == '\t' ? "\\t" : c.ToString()) + "'",
        _ => value.ToString() ?? "",
    };
}

internal static class Runner
{
    public static int Run(Type[] containers, string? filter = null)
    {
        int passed = 0, failed = 0;
        var sw = Stopwatch.StartNew();
        foreach (var type in containers)
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                         .Where(m => m.GetCustomAttribute<CaseAttribute>() is not null)
                         .OrderBy(m => m.MetadataToken))
            {
                var name = type.Name + "." + m.Name;
                if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                var t = Stopwatch.StartNew();
                try
                {
                    m.Invoke(null, null);
                    passed++;
                    Console.WriteLine($"  ok    {name} ({t.ElapsedMilliseconds} ms)");
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    failed++;
                    Console.WriteLine($"  FAIL  {name}: {ex.InnerException.Message}");
                    if (ex.InnerException is not CheckFailed) Console.WriteLine(ex.InnerException.StackTrace);
                }
            }
        }

        Console.WriteLine($"{passed} passed, {failed} failed in {sw.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }
}
