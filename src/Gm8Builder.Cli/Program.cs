using System.Diagnostics;
using Gm8Builder.Build;
using Gm8Builder.Exe;
using Gm8Builder.Model;

namespace Gm8Builder.Cli;

public static class Program
{
    private const string Usage = """
        usage:
          gm8-builder info <game.exe>          summarise a GM8.0 executable's contents
          gm8-builder roundtrip <game.exe>     read it, write it back, and check the two agree
          gm8-builder build <tree> <out.exe> [--gm8 <dir>] [--template <game.exe>] [--gm8x-fix] [--lint]
                                              build a split tree. The runner, DLL, extensions and
                                              library init code come from a Game Maker 8.0 install
                                              (--gm8, or GM8_DIR), else from an earlier build.
                                              --gm8x-fix applies gm8x_fix's runner patches;
                                              --lint checks the tree's GML first, and stops on errors
          gm8-builder lint <file-or-dir>... [options]
                                              check GML against Game Maker 8 (lint --help for more)
          gm8-builder compare <a.exe> <b.exe> [--limit N]
                                              list differences in content, N per section (default 10)
        """;

    public static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["info", var exe] => Info(exe),
                ["roundtrip", var exe] => RoundTrip(exe),
                ["build", var tree, var output, .. var rest] => BuildTree(tree, output, rest),
                ["lint", .. var rest] => LintCommand.Run(rest),
                ["compare", var a, var b] => Compare.Run(ExeFile.Read(File.ReadAllBytes(a)), ExeFile.Read(File.ReadAllBytes(b)), 10),
                ["compare", var a, var b, "--limit", var n] =>
                    Compare.Run(ExeFile.Read(File.ReadAllBytes(a)), ExeFile.Read(File.ReadAllBytes(b)), int.Parse(n)),
                _ => Fail(Usage),
            };
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return Fail($"gm8-builder: {e.Message}");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int Info(string path)
    {
        var sw = Stopwatch.StartNew();
        var exe = ExeFile.Read(File.ReadAllBytes(path));
        var d = exe.Data;
        Console.WriteLine($"read in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"runner      {exe.Runner.Length:N0} bytes, dll {exe.DllName} {exe.Dll.Length:N0} bytes");
        Console.WriteLine($"game id     {d.GameId}, pro {d.Pro}");
        Console.WriteLine($"extensions  {string.Join(", ", d.Extensions.Select(e => $"{e.Name} ({e.Files.Count} files)"))}");
        Line("triggers", d.Triggers);
        Console.WriteLine($"constants   {d.Constants.Count}");
        Line("sounds", d.Sounds);
        Line("sprites", d.Sprites);
        Line("backgrounds", d.Backgrounds);
        Line("paths", d.Paths);
        Line("scripts", d.Scripts);
        Line("fonts", d.Fonts);
        Line("timelines", d.Timelines);
        Line("objects", d.Objects);
        Line("rooms", d.Rooms);
        Console.WriteLine($"included    {d.IncludedFiles.Count}");
        Console.WriteLine($"lib init    {d.LibraryInit.Count} strings");
        Console.WriteLine($"room order  {d.RoomOrder.Count}");
        Console.WriteLine($"last ids    instance {d.LastInstanceId}, tile {d.LastTileId}");

        var actions = d.Objects.OfType<GameObject>()
            .SelectMany(o => o.Events.SelectMany(l => l.SelectMany(e => e.Actions)))
            .Concat(d.Timelines.OfType<Timeline>().SelectMany(t => t.Moments.SelectMany(m => m.Actions)))
            .GroupBy(a => (a.LibraryId, a.ActionId))
            .OrderByDescending(g => g.Count());
        Console.WriteLine("actions     " + string.Join(", ", actions.Select(g => $"{g.Key.LibraryId}/{g.Key.ActionId} x{g.Count()}")));
        return 0;
    }

    private static int BuildTree(string tree, string output, string[] options)
    {
        string? gm8 = Environment.GetEnvironmentVariable("GM8_DIR"), templatePath = null;
        bool fix = false, lint = false;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--gm8" when i + 1 < options.Length:
                    gm8 = options[++i];
                    break;
                case "--template" when i + 1 < options.Length:
                    templatePath = options[++i];
                    break;
                case "--gm8x-fix":
                    fix = true;
                    break;
                case "--lint":
                    lint = true;
                    break;
                default:
                    return Fail(Usage);
            }
        }
        if (string.IsNullOrEmpty(gm8) && templatePath == null)
            return Fail("gm8-builder: build needs --gm8 <Game Maker 8 directory> (or GM8_DIR), or --template <earlier build>");

        var sw = Stopwatch.StartNew();
        if (lint)
        {
            var findings = LintCommand.CheckTree(tree, gm8);
            if (findings.Count > 0) LintCommand.Print(findings);
            if (findings.Any(f => f.IsError)) return Fail("gm8-builder: not built - the GML has errors");
            sw.Restart();
        }
        var install = string.IsNullOrEmpty(gm8) ? null : new Gm8Builder.Install.Gm8Install(gm8);
        var template = templatePath == null ? null : ExeFile.Read(File.ReadAllBytes(templatePath));
        var result = Builder.Build(tree, install, template, gm8xFix: fix);
        var built = sw.ElapsedMilliseconds;
        sw.Restart();
        var bytes = result.Exe.Write();
        File.WriteAllBytes(output, bytes);
        foreach (var w in result.Warnings) Console.Error.WriteLine($"warning: {w}");
        Console.WriteLine($"{output}: {bytes.Length:N0} bytes (read {built} ms, write {sw.ElapsedMilliseconds} ms)");
        return 0;
    }

    private static void Line<T>(string name, List<T?> list) where T : class =>
        Console.WriteLine($"{name,-11} {list.Count(a => a != null)} ({list.Count} slots)");

    /// <summary>
    /// Read, write back with the compressed-blob cache, and require the exact
    /// original bytes; then write without the cache, re-read, and require the
    /// same decompressed stream - which is what a real rebuild does.
    /// </summary>
    private static int RoundTrip(string path)
    {
        var original = File.ReadAllBytes(path);
        var sw = Stopwatch.StartNew();
        var cache = new BlobCache();
        var exe = ExeFile.Read(original, cache);
        var read = sw.ElapsedMilliseconds;

        sw.Restart();
        var cached = exe.Write(cache);
        var writeCached = sw.ElapsedMilliseconds;
        var identical = cached.AsSpan().SequenceEqual(original);
        Console.WriteLine($"read {read} ms, write (cached) {writeCached} ms: {(identical ? "byte-identical" : "DIFFERENT")}");
        if (!identical) Console.WriteLine($"  first difference at 0x{FirstDifference(cached, original):x} ({cached.Length:N0} vs {original.Length:N0} bytes)");

        sw.Restart();
        var fresh = exe.Write();
        var writeFresh = sw.ElapsedMilliseconds;
        var again = ExeFile.Read(fresh);
        var sameStream = again.Data.Write().AsSpan().SequenceEqual(exe.Data.Write());
        Console.WriteLine($"write (recompressed) {writeFresh} ms, {fresh.Length:N0} bytes vs {original.Length:N0}: stream {(sameStream ? "identical" : "DIFFERENT")}");
        return identical && sameStream ? 0 : 1;
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            if (a[i] != b[i]) return i;
        }
        return n;
    }
}
