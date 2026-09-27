using System.Text.RegularExpressions;
using Gm8Builder.Install;
using Gm8Builder.Tree;

namespace Gm8Builder.Lint;

/// <summary>What to lint against: the engine, the project, and any extension functions.</summary>
public sealed record LintOptions
{
    /// <summary>Split trees whose symbols are in scope. More than one merges them - a tree plus something injected into it, say.</summary>
    public IReadOnlyList<string> Trees { get; init; } = [];

    /// <summary>
    /// Files listing further extension functions, one per line, '#' comments. The
    /// functions of the packages a tree uses are read from the install without this.
    /// </summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>The Game Maker 8 install; GM8_DIR if null.</summary>
    public string? Gm8Dir { get; init; }

    public bool Arity { get; init; } = true;
    public bool Style { get; init; }
}

/// <summary>A lint answer. `Note` is set, and `Ok` true, when linting could not run at all.</summary>
public sealed record LintResult(bool Ok, IReadOnlyList<Finding> Findings, string? Note = null)
{
    public IEnumerable<Finding> Errors => Findings.Where(f => f.IsError);
}

/// <summary>
/// Builds lint contexts and keeps them. Building one means reading fnames and
/// walking every tree, so a long-running caller - `gm8-builder lint --serve` - reuses
/// them, keyed on what they were built from.
///
/// The key includes a cheap signature of each tree: the modification times of
/// its resource directories. Adding, removing or renaming a script, object,
/// sprite or any other resource changes its directory's time (NTFS updates a
/// directory whenever an entry in it does), so a new script is in scope on the
/// very next call, at the cost of a stat per directory rather than a full walk.
/// Editing a file's contents does not move it - so a `globalvar` newly added to
/// an existing script is picked up only once some resource is added or removed,
/// or the server restarts.
/// </summary>
public sealed partial class LintEnvironment
{
    private const int CacheSize = 8;
    private readonly List<(string Key, LintContext Context)> _cache = [];
    private readonly Lock _lock = new();

    private static readonly string[] ResourceDirs =
        ["Scripts", "Objects", "Sprites", "Sounds", "Backgrounds", "Rooms", "Fonts", "Time Lines", "Paths"];

    /// <summary>Check a snippet, or every STRING argument of an event file when `xml` is set.</summary>
    public LintResult Check(string code, LintOptions options, bool xml = false, string name = "<gml>")
    {
        var gm8 = FindInstall(options.Gm8Dir);
        if (gm8 == null) return new LintResult(true, [], "lint unavailable: no Game Maker 8 install given (it needs the \"fnames\" file; pass --gm8 or set GM8_DIR)");
        var trees = options.Trees.Where(t => !string.IsNullOrEmpty(t)).Select(Path.GetFullPath).ToList();
        if (trees.Count == 0) return new LintResult(true, [], "lint unavailable: no source tree given");

        List<Finding> findings;
        try
        {
            var ctx = Context(trees, options, gm8);
            findings = xml ? Linter.LintEventXml(code, ctx, name) : Linter.LintSource(code, ctx, name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new LintResult(true, [], "lint unavailable: " + e.Message);
        }
        return new LintResult(!findings.Any(f => f.IsError), findings);
    }

    /// <summary>The install: the one given, else GM8_DIR - provided it has an fnames file.</summary>
    public static string? FindInstall(string? explicitDir)
    {
        var dir = !string.IsNullOrEmpty(explicitDir) ? explicitDir : Environment.GetEnvironmentVariable("GM8_DIR");
        return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "fnames")) ? Path.GetFullPath(dir) : null;
    }

    public LintContext Context(IReadOnlyList<string> trees, LintOptions options, string gm8Dir)
    {
        // Order must not matter: two callers passing the same trees in a
        // different sequence describe the same project and must share an entry.
        var sorted = trees.Order(StringComparer.Ordinal).ToList();
        var extensions = options.Extensions.Select(Path.GetFullPath).Order(StringComparer.Ordinal).ToList();
        var key = string.Join("\n",
        [
            gm8Dir, options.Arity.ToString(), options.Style.ToString(),
            .. sorted.Select(t => t + "@" + Signature(t)),
            .. extensions.Select(e => $"{e}@{Stamp(e)}"),
        ]);

        lock (_lock)
        {
            foreach (var (k, c) in _cache)
            {
                if (k == key) return c;
            }
        }

        var symbols = new ProjectSymbols();
        foreach (var tree in sorted) LoadProject(tree, symbols);
        var extensionFunctions = LoadExtensions(extensions);
        extensionFunctions.UnionWith(LoadInstalledExtensions(gm8Dir, sorted));
        var ctx = new LintContext
        {
            Builtins = LoadBuiltins(Path.Combine(gm8Dir, "fnames"), gm8Dir),
            Symbols = symbols,
            Extensions = extensionFunctions,
            Arity = options.Arity,
            Style = options.Style,
        };

        lock (_lock)
        {
            _cache.Add((key, ctx));
            if (_cache.Count > CacheSize) _cache.RemoveAt(0);
        }
        return ctx;
    }

    /// <summary>One stat. Works for a directory too, and gives a fixed value for a path that does not exist.</summary>
    private static long Stamp(string path) => File.GetLastWriteTimeUtc(path).Ticks;

    /// <summary>The paths a tree's signature is made of, and their stamps when last walked.</summary>
    private sealed record TreeWatch(string[] Paths, long[] Stamps, string Signature);

    private readonly Dictionary<string, TreeWatch> _watches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tree's own directory, Constants.xml, Extension Packages.xml, and every resource group
    /// directory, as one string of modification times. The list of directories is
    /// kept between calls and only walked again when a stamp moves - adding or
    /// removing a group changes its parent's stamp, so a stale list can never
    /// miss one - which makes an unchanged tree cost one stat per directory.
    /// </summary>
    internal string Signature(string tree)
    {
        TreeWatch? watch;
        lock (_lock) _watches.TryGetValue(tree, out watch);
        if (watch != null)
        {
            var unchanged = true;
            for (var i = 0; i < watch.Paths.Length && unchanged; i++) unchanged = Stamp(watch.Paths[i]) == watch.Stamps[i];
            if (unchanged) return watch.Signature;
        }

        var paths = new List<string> { tree, Path.Combine(tree, "Constants.xml"), Path.Combine(tree, "Extension Packages.xml") };
        foreach (var top in ResourceDirs)
        {
            var dir = Path.Combine(tree, top);
            if (Directory.Exists(dir)) AddGroupDirectories(dir, paths);
        }
        var stamps = paths.Select(Stamp).ToArray();
        watch = new TreeWatch([.. paths], stamps, $"{paths.Count}:{string.Join(",", stamps)}");
        lock (_lock) _watches[tree] = watch;
        return watch.Signature;
    }

    private static void AddGroupDirectories(string dir, List<string> paths)
    {
        paths.Add(dir);
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (!IsResourceData(sub)) AddGroupDirectories(sub, paths);
        }
    }

    //---------------------------------------------------------------------------
    // GM8 built-ins, read from the engine's own fnames table
    //---------------------------------------------------------------------------

    /// <summary>
    /// fnames, plus - given the install - the functions its action libraries'
    /// actions call (action_move and the like). The runner has those, and GML may
    /// call them, but fnames does not list them; their arity is unknown.
    /// </summary>
    public static Builtins LoadBuiltins(string fnamesPath, string? gm8Dir = null)
    {
        var b = new Builtins();
        if (gm8Dir != null)
        {
            foreach (var lib in ActionLibrary.ReadAll(gm8Dir)) b.Names.UnionWith(lib.Functions);
        }
        foreach (var raw in Latin1.Read(fnamesPath).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;

            var call = FnamesCall().Match(line);
            if (call.Success)
            {
                var fn = call.Groups[1].Value;
                var inner = call.Groups[2].Value.Trim();
                var parts = inner.Length == 0 ? [] : inner.Split(',').Select(s => s.Trim()).ToList();
                var variadic = parts.Any(p => p == "..." || p.EndsWith("...", StringComparison.Ordinal));
                var named = parts.Where(p => p != "...").ToList();
                int min;
                if (variadic)
                {
                    // In a signature like execute_string(str,arg0,arg1,...) the
                    // numbered tail illustrates the variadic part rather than being
                    // required, so trailing numbered parameters do not count.
                    while (named.Count > 0 && NumberedParameter().IsMatch(named[^1])) named.RemoveAt(named.Count - 1);
                    min = Math.Max(1, named.Count);
                }
                else
                {
                    min = named.Count;
                }
                b.Funcs[fn] = new Arity(min, variadic ? int.MaxValue : named.Count);
                b.Names.Add(fn);
                continue;
            }

            // Constants end with '#', assignable built-in variables with '*'.
            var bare = TrailingMarker().Replace(ArraySuffix().Replace(line, ""), "");
            b.Names.Add(bare);
            if (!line.EndsWith('#')) b.Vars.Add(bare);
        }
        return b;
    }

    public static HashSet<string> LoadExtensions(IEnumerable<string> files)
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length > 0 && !line.StartsWith('#')) output.Add(line);
            }
        }
        return output;
    }

    /// <summary>
    /// The functions and constants of every installed extension package the trees
    /// name in their Extension Packages.xml - the same packages a build embeds.
    /// </summary>
    public static HashSet<string> LoadInstalledExtensions(string gm8Dir, IEnumerable<string> trees)
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        var dir = Path.Combine(gm8Dir, "extensions");
        if (!Directory.Exists(dir)) return output;

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in trees)
        {
            var list = Path.Combine(tree, "Extension Packages.xml");
            if (!File.Exists(list)) continue;
            foreach (var p in Xml.Load(list).Expect("extensionPackages").Children("package")) wanted.Add(p.Text);
        }
        if (wanted.Count == 0) return output;

        foreach (var ged in Directory.EnumerateFiles(dir, "*.ged"))
        {
            var package = ExtensionPackage.ReadDescription(ged);
            if (!wanted.Contains(package.Name)) continue;
            foreach (var f in package.Files)
            {
                foreach (var fn in f.Functions) output.Add(fn.Name);
                foreach (var c in f.Constants) output.Add(c.Name);
            }
        }
        return output;
    }

    //---------------------------------------------------------------------------
    // Project symbols, read from a split source tree
    //---------------------------------------------------------------------------

    /// <summary>Add one tree's symbols to `into`.</summary>
    public static void LoadProject(string tree, ProjectSymbols into)
    {
        foreach (var dir in ResourceDirs) CollectResourceNames(Path.Combine(tree, dir), into.Resources);

        var constants = Path.Combine(tree, "Constants.xml");
        if (File.Exists(constants))
        {
            foreach (Match m in ConstantName().Matches(File.ReadAllText(constants))) into.Resources.Add(m.Groups[1].Value);
        }

        into.All.UnionWith(into.Resources);

        // globalvar declarations anywhere in the tree are legitimate globals - but
        // not resources: a globalvar is deliberately assignable.
        if (!Directory.Exists(tree)) return;
        foreach (var f in Directory.EnumerateFiles(tree, "*.gml", SearchOption.AllDirectories))
        {
            foreach (Match m in GlobalVar().Matches(Latin1.Read(f)))
            {
                foreach (var n in m.Groups[1].Value.Split(',')) into.All.Add(n.Trim());
            }
        }
    }

    /// <summary>
    /// A directory that belongs to one resource rather than grouping several: an
    /// object's `Name.events/`, a sprite's `Name.images/` frames. Neither holds
    /// names, and a sprite-heavy tree has hundreds of the second kind - often most
    /// of its directories - which made stat-ing them the whole cost of a lint
    /// request.
    /// </summary>
    private static bool IsResourceData(string dir) =>
        dir.EndsWith(".events", StringComparison.Ordinal) || dir.EndsWith(".images", StringComparison.Ordinal);

    /// <summary>Resource names are file names; groups are directories.</summary>
    private static void CollectResourceNames(string dir, HashSet<string> into)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (entry.Name == "_resources.list.xml") continue;
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                if (!IsResourceData(entry.Name)) CollectResourceNames(entry.FullName, into);
                continue;
            }
            into.Add(ResourceExtension().Replace(entry.Name, ""));
        }
    }

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*)\((.*)\)\s*$")]
    private static partial Regex FnamesCall();

    [GeneratedRegex(@"^[A-Za-z_]*\d+$")]
    private static partial Regex NumberedParameter();

    [GeneratedRegex(@"\[.*\]$")]
    private static partial Regex ArraySuffix();

    [GeneratedRegex("[#*&@]$")]
    private static partial Regex TrailingMarker();

    [GeneratedRegex("name=\"([^\"]+)\"")]
    private static partial Regex ConstantName();

    [GeneratedRegex(@"\bglobalvar\s+([^;]+);")]
    private static partial Regex GlobalVar();

    [GeneratedRegex(@"\.(gml|xml|png|wav|mp3|mid|ico)$", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceExtension();
}
