using System.Collections;
using System.Reflection;
using Gm8Builder.Exe;
using Gm8Builder.IO;
using Gm8Builder.Model;

namespace Gm8Builder.Cli;

/// <summary>
/// Asset-by-asset comparison of two executables' contents - the check that a
/// build matches what Game Maker produced. Random parts (cipher table, filler)
/// and compression are not compared.
/// </summary>
public static class Compare
{
    public static int Run(ExeFile a, ExeFile b, int limit)
    {
        var report = new Report(limit);
        report.Diff("runner", a.Runner, b.Runner);
        report.Diff("settings", Settings(a.Settings), Settings(b.Settings));
        report.Diff("dll", (a.DllName, a.Dll), (b.DllName, b.Dll));

        var x = a.Data;
        var y = b.Data;
        report.Diff("game id", x.GameId, y.GameId);
        report.Diff("guid", x.Guid, y.Guid);
        report.Diff("pro", x.Pro, y.Pro);
        report.List("extensions", x.Extensions, y.Extensions, e => e.Name);
        report.List("triggers", x.Triggers, y.Triggers, t => t.Name);
        report.List("constants", x.Constants, y.Constants, c => c.Name);
        report.List("sounds", x.Sounds, y.Sounds, s => s.Name);
        report.List("sprites", x.Sprites, y.Sprites, s => s.Name);
        report.List("backgrounds", x.Backgrounds, y.Backgrounds, s => s.Name);
        report.List("paths", x.Paths, y.Paths, s => s.Name);
        report.List("scripts", x.Scripts, y.Scripts, s => s.Name);
        report.List("fonts", x.Fonts, y.Fonts, s => s.Name);
        report.List("timelines", x.Timelines, y.Timelines, s => s.Name);
        report.List("objects", x.Objects, y.Objects, s => s.Name);
        report.List("rooms", x.Rooms, y.Rooms, s => s.Name);
        report.Diff("last instance id", x.LastInstanceId, y.LastInstanceId);
        report.Diff("last tile id", x.LastTileId, y.LastTileId);
        report.List("included files", x.IncludedFiles, y.IncludedFiles, f => f.FileName);
        report.Diff("game information", x.Information, y.Information);
        report.Diff("library init", x.LibraryInit, y.LibraryInit);
        report.Diff("room order", x.RoomOrder, y.RoomOrder);
        return report.Finish();
    }

    /// <summary>The settings with their images inflated, so compression does not count as a difference.</summary>
    private static Settings Settings(Settings s)
    {
        var copy = (Settings)typeof(Settings).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(s, null)!;
        copy.LoadingImage = s.LoadingImage is { } i ? Zlib.Inflate(i) : null;
        copy.LoadingBarBack = s.LoadingBarBack is { } bk ? Zlib.Inflate(bk) : null;
        copy.LoadingBarFront = s.LoadingBarFront is { } f ? Zlib.Inflate(f) : null;
        return copy;
    }

    private sealed class Report(int limit)
    {
        private int _differences;
        private readonly Dictionary<string, int> _bySection = [];

        public void Diff<T>(string what, T a, T b)
        {
            if (First(a, b, "") is { } d) Print(what, d);
        }

        public void List<T>(string what, IReadOnlyList<T?> a, IReadOnlyList<T?> b, Func<T, string> name) where T : class
        {
            if (a.Count != b.Count) Print(what, $"{a.Count} vs {b.Count} slots");
            for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                var label = $"{what}[{i}] {(a[i] ?? b[i]) switch { null => "", var t => name(t) }}";
                if (a[i] == null && b[i] == null) continue;
                if (a[i] == null || b[i] == null)
                {
                    Print(what, $"{label}: {(a[i] == null ? "missing" : "present")} vs {(b[i] == null ? "missing" : "present")}");
                    continue;
                }
                if (First(a[i], b[i], "") is { } d) Print(what, $"{label}: {d}");
            }
        }

        private void Print(string section, string detail)
        {
            _differences++;
            _bySection[section] = _bySection.GetValueOrDefault(section) + 1;
            if (_bySection[section] <= limit) Console.WriteLine($"{section}: {detail}");
        }

        public int Finish()
        {
            foreach (var (section, n) in _bySection.Where(kv => kv.Value > limit))
                Console.WriteLine($"{section}: ... {n - limit} more");
            Console.WriteLine(_differences == 0 ? "identical" : $"{_differences} differences");
            return _differences == 0 ? 0 : 1;
        }
    }

    /// <summary>The first difference between two model values, as "path: a vs b", or null.</summary>
    internal static string? First(object? a, object? b, string path)
    {
        if (a == null || b == null) return a == b ? null : $"{Path(path)}: {Show(a)} vs {Show(b)}";
        if (a is string || a.GetType().IsPrimitive || a is Enum) return Equals(a, b) ? null : $"{Path(path)}: {Show(a)} vs {Show(b)}";

        if (a is byte[] ba && b is byte[] bb)
        {
            if (ba.AsSpan().SequenceEqual(bb)) return null;
            var n = Math.Min(ba.Length, bb.Length);
            var at = 0;
            while (at < n && ba[at] == bb[at]) at++;
            var count = 0;
            for (var i = 0; i < n; i++) count += ba[i] != bb[i] ? 1 : 0;
            var last = n - 1;
            while (last > at && ba[last] == bb[last]) last--;
            return at < n
                ? $"{Path(path)}: {count} of {ba.Length} bytes differ, in [0x{at:x}, 0x{last + 1:x}), first {ba[at]} vs {bb[at]}{(ba.Length != bb.Length ? $" (length {ba.Length} vs {bb.Length})" : "")}"
                : $"{Path(path)}: length {ba.Length} vs {bb.Length}";
        }

        if (a is IList la && b is IList lb)
        {
            for (var i = 0; i < Math.Min(la.Count, lb.Count); i++)
            {
                if (First(la[i], lb[i], $"{path}[{i}]") is { } d) return d;
            }
            return la.Count == lb.Count ? null : $"{Path(path)}: count {la.Count} vs {lb.Count}";
        }

        var type = a.GetType();
        if (type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple"))
        {
            foreach (var f in type.GetFields())
            {
                if (First(f.GetValue(a), f.GetValue(b), $"{path}.{f.Name}") is { } d) return d;
            }
            return null;
        }
        foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (First(f.GetValue(a), f.GetValue(b), $"{path}.{f.Name}") is { } d) return d;
        }
        foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetIndexParameters().Length == 0))
        {
            if (First(p.GetValue(a), p.GetValue(b), $"{path}.{p.Name}") is { } d) return d;
        }
        return null;
    }

    private static string Path(string p) => p == "" ? "value" : p.TrimStart('.');

    private static string Show(object? v) => v switch
    {
        null => "null",
        string s => $"\"{(s.Length > 60 ? s[..60] + "..." : s).Replace("\r", "\\r").Replace("\n", "\\n")}\"",
        _ => v.ToString() ?? "",
    };
}
