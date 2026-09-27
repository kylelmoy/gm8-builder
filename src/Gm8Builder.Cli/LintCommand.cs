using System.Text;
using System.Text.Json;
using Gm8Builder.Lint;

namespace Gm8Builder.Cli;

/// <summary>`gm8-builder lint`, and the check `build --lint` runs first.</summary>
public static class LintCommand
{
    public const string Usage = """
        usage: gm8-builder lint <file-or-dir>... [options]
               gm8-builder lint --stdin [options]     lint GML read from stdin
               gm8-builder lint --serve [options]     answer JSON requests on stdin, one per line

          --tree <dir>        a split tree whose symbols are in scope (repeatable; default: the
                              nearest directory above the first target, or the current one, with
                              an Objects/ directory in it)
          --extensions <f>    a file listing further extension functions (repeatable); those of
                              the installed packages the tree uses are known without it
          --gm8 <dir>         the Game Maker 8 install, for its fnames file (default: GM8_DIR)
          --no-arity          skip argument-count checks
          --style             also warn about symbol operators (&&, ||, ^^, !)
          --json              machine-readable findings

        --serve reads one JSON object per line - {"id", "code", "xml", "name", "trees",
        "extensions", "gm8", "arity", "style"}, everything but "code" optional and
        defaulting to the command line's - and answers each with one line:
        {"id", "ok", "findings", "errors", "note"?, "error"?}. The context is built once
        and refreshed when a resource is added to or removed from a tree.
        """;

    public static int Run(string[] argv)
    {
        var trees = new List<string>();
        var extensions = new List<string>();
        var targets = new List<string>();
        string? gm8 = null;
        bool noArity = false, style = false, json = false, stdin = false, serve = false;
        for (var i = 0; i < argv.Length; i++)
        {
            string Value() => i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{argv[i]} needs a value");
            switch (argv[i])
            {
                case "--tree": trees.Add(Path.GetFullPath(Value())); break;
                case "--extensions": extensions.Add(Value()); break;
                case "--gm8": gm8 = Value(); break;
                case "--no-arity": noArity = true; break;
                case "--style": style = true; break;
                case "--json": json = true; break;
                case "--stdin": stdin = true; break;
                case "--serve": serve = true; break;
                case "--help" or "-h":
                    Console.WriteLine(Usage);
                    return 0;
                case var a when a.StartsWith("--", StringComparison.Ordinal):
                    throw new ArgumentException($"unknown option {a}");
                default: targets.Add(argv[i]); break;
            }
        }

        if (trees.Count == 0 && FindTree(targets.FirstOrDefault() ?? Environment.CurrentDirectory) is { } found) trees.Add(found);
        var options = new LintOptions
        {
            Trees = trees,
            Extensions = extensions,
            Gm8Dir = gm8,
            Arity = !noArity,
            Style = style,
        };
        var env = new LintEnvironment();

        if (serve)
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            LintServer.Run(input, output, env, options);
            return 0;
        }

        if (LintEnvironment.FindInstall(gm8) == null)
        {
            Console.Error.WriteLine("gm8-builder lint: no Game Maker 8 install with an \"fnames\" file. Pass --gm8 <dir> or set GM8_DIR.");
            return 2;
        }
        if (trees.Count == 0)
        {
            Console.Error.WriteLine("gm8-builder lint: could not find the split source tree. Pass --tree <dir>.");
            return 2;
        }

        List<Finding> findings;
        if (stdin)
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            findings = [.. env.Check(input.ReadToEnd(), options, name: "<stdin>").Findings];
        }
        else
        {
            if (targets.Count == 0) throw new ArgumentException("nothing to lint. Pass a file or directory, or --stdin.");
            findings = targets.SelectMany(t => Check(env, options, t)).ToList();
        }

        var errors = findings.Count(f => f.IsError);
        if (json)
        {
            var response = new LintResponse { Ok = errors == 0, Findings = findings, Errors = findings.Where(f => f.IsError).ToList() };
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            output.WriteLine(JsonSerializer.Serialize(response, LintJson.Relaxed.LintResponse));
        }
        else if (findings.Count == 0)
        {
            Console.WriteLine("gm8-builder lint: clean");
        }
        else
        {
            Print(findings);
        }
        return errors > 0 ? 1 : 0;
    }

    /// <summary>Every script and event file of a tree, for `build --lint`.</summary>
    public static List<Finding> CheckTree(string tree, string? gm8)
    {
        var options = new LintOptions { Trees = [Path.GetFullPath(tree)], Gm8Dir = gm8 };
        if (LintEnvironment.FindInstall(gm8) == null)
            throw new InvalidDataException("--lint needs a Game Maker 8 install with an \"fnames\" file (--gm8 or GM8_DIR)");
        return Check(new LintEnvironment(), options, tree).ToList();
    }

    public static void Print(IReadOnlyList<Finding> findings)
    {
        foreach (var f in findings) Console.WriteLine($"{f.File}:{f.Line}:{f.Col}: {f.Severity}: {f.Message} [{f.Rule}]");
        var errors = findings.Count(f => f.IsError);
        Console.WriteLine($"\n{errors} error(s), {findings.Count - errors} warning(s)");
    }

    private static IEnumerable<Finding> Check(LintEnvironment env, LintOptions options, string target)
    {
        foreach (var f in Targets(target))
        {
            var text = Latin1.Read(f);
            var isXml = f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
            if (isXml && !text.Contains("<argument kind=\"STRING\">", StringComparison.Ordinal)) continue;
            foreach (var finding in env.Check(text, options, isXml, Path.GetRelativePath(Environment.CurrentDirectory, f)).Findings)
                yield return finding;
        }
    }

    /// <summary>The nearest directory at or above `start` that looks like a split tree.</summary>
    private static string? FindTree(string start)
    {
        var dir = Path.GetFullPath(start);
        if (File.Exists(dir)) dir = Path.GetDirectoryName(dir)!;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "Objects"))) return d.FullName;
        }
        return null;
    }

    private static IEnumerable<string> Targets(string target)
    {
        if (File.Exists(target)) return [Path.GetFullPath(target)];
        if (!Directory.Exists(target)) throw new ArgumentException($"not found: {target}");
        return Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".gml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) &&
                        !f.EndsWith("_resources.list.xml", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath);
    }
}
