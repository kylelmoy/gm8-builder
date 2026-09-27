using Gm8Builder.Lint;

namespace Gm8Builder.Tests;

/// <summary>
/// The linter against a fake engine (a small fnames) and the fixture tree:
/// both the mistakes it must catch and the look-alike idioms that must pass.
/// </summary>
public sealed class LinterTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly LintEnvironment _env = new();
    private readonly LintOptions _options;

    public LinterTests()
    {
        var extensions = _dir.Write("ext.txt", "# a .gex function\nfaucet_send\n");
        _options = new LintOptions { Trees = [Fixtures.Tree], Extensions = [extensions], Gm8Dir = Fixtures.FakeGm8(_dir) };
    }

    public void Dispose() => _dir.Dispose();

    private LintResult Lint(string code) => _env.Check(code, _options);

    private void Bad(string code, string rule)
    {
        var r = Lint(code);
        Assert.True(!r.Ok && r.Errors.Any(e => e.Rule == rule),
            $"expected {rule} for {code}, got: {string.Join("; ", r.Findings.Select(f => f.Rule + " " + f.Message))}");
    }

    private void Good(string code)
    {
        var r = Lint(code);
        Assert.True(r.Ok, $"expected {code} to pass, got: {string.Join("; ", r.Errors.Select(f => f.Rule + " " + f.Message))}");
    }

    [Theory]
    [InlineData("a = (1 + );", "dangling-operator")]
    [InlineData("x = * 5;", "dangling-operator")]
    [InlineData("z = 5 or or 6;", "dangling-operator")]
    [InlineData("return (1 ; 2);", "semicolon-in-expression")]
    [InlineData("y = (1, 2);", "comma-in-grouping")]
    [InlineData("x = (a, b, c);", "comma-in-grouping")]
    [InlineData("if (a, b) { exit; }", "comma-in-grouping")]
    [InlineData("x = a ? 1 : 2;", "ternary")]
    [InlineData("x = (1;", "unbalanced")]
    [InlineData("s = \"never closed;", "unterminated-string")]
    [InlineData("/* never closed", "unterminated-comment")]
    [InlineData("if (a &gt; b) { exit; }", "html-entity")]
    [InlineData("new = 1;", "future-reserved")]
    [InlineData("n = array_length(a);", "modern-function")]
    [InlineData("nothingDefinesThis();", "unknown-function")]
    [InlineData("x = point_distance(1, 2);", "arity")]
    [InlineData("var room_speed;", "var-shadows-builtin")]
    [InlineData("var x;", "var-shadows-builtin")]
    [InlineData("helper = 1;", "assign-to-resource-name")]
    [InlineData("MAX_PLAYERS += 1;", "assign-to-resource-name")]
    public void Refuses(string code, string rule) => Bad(code, rule);

    [Theory]
    [InlineData("for (i = 0; i < 10; i += 1) { x += 1; }")]
    [InlineData("a[i, j] = 5;")]
    [InlineData("x = -y;")]
    [InlineData("z = 1 - -1;")]
    [InlineData("if (not flag) { exit; }")]
    [InlineData("switch (x) { case 1: break; default: break; }")]
    [InlineData("do { i += 1; } until (i >= 10);")]
    [InlineData("var i, j; i = 0; j = 0;")]
    [InlineData("with (self) { x = 1; }")]
    [InlineData("a = b == c;")]
    [InlineData("a = !b;")]
    [InlineData("if (a = b) { exit; }")]
    [InlineData("x = point_distance(a, b, c, d);")]
    [InlineData("x = ds_grid_get(grid, a[i, j], y);")]
    [InlineData("y = (a[i, j]);")]
    [InlineData("x = helper(1) + max(1, 2, 3);")]
    [InlineData("execute_string(\"x = 1;\");")]
    [InlineData("faucet_send();")]
    [InlineData("gScore = 1;")] // a globalvar is assignable, unlike a resource
    [InlineData("if (sprite_index == sprPlayer) { exit; }")]
    [InlineData("x = $FF + .5 + pi;")]
    public void Accepts(string code) => Good(code);

    /// <summary>
    /// A bracket that opens directly inside a call starts an argument: `abs((y))`
    /// has one, not none.
    /// </summary>
    [Theory]
    [InlineData("x = abs((y));")]
    [InlineData("x = abs([1]);")]
    [InlineData("x = point_distance((a), b, c, d);")]
    public void A_bracketed_argument_counts_as_an_argument(string code) => Good(code);

    [Fact]
    public void Style_is_only_reported_when_asked_for()
    {
        Assert.Empty(Lint("if (a && !b) { exit; }").Findings);
        var styled = _env.Check("if (a && !b) { exit; }", _options with { Style = true });
        Assert.True(styled.Ok);
        Assert.Equal(2, styled.Findings.Count(f => f.Severity == "warning"));
    }

    [Fact]
    public void Findings_in_event_xml_point_into_the_xml_file()
    {
        var xml = "<event>\n  <action>\n    <argument kind=\"STRING\">x = 1;\nnothingDefinesThis();</argument>\n  </action>\n</event>";
        var r = _env.Check(xml, _options, xml: true, name: "Step.xml");
        var f = Assert.Single(r.Errors);
        Assert.Equal(4, f.Line);
        Assert.Equal("Step.xml", f.File);
    }

    [Fact]
    public void A_missing_install_is_a_note_not_a_failure()
    {
        var r = _env.Check("anything(", _options with { Gm8Dir = _dir.Combine("nowhere") });
        Assert.True(r.Ok);
        Assert.Contains("no Game Maker 8 install", r.Note);
    }

    [Fact]
    public void Tokens_carry_their_position()
    {
        var toks = Tokenizer.Tokenize("a = \"x\ny\";\n  b");
        Assert.Equal(new Token(TokenType.Ident, "a", 1, 1), toks[0]);
        Assert.Equal(new Token(TokenType.String, "x\ny", 1, 5), toks[2]);
        Assert.Equal(new Token(TokenType.Ident, "b", 3, 3), toks[^1]);
    }
}

public sealed class LintEnvironmentTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_script_added_to_a_nested_group_is_in_scope_on_the_next_call()
    {
        var tree = Fixtures.CopyTree(_dir);
        var env = new LintEnvironment();
        var options = new LintOptions { Trees = [tree], Gm8Dir = Fixtures.FakeGm8(_dir) };

        Assert.False(env.Check("brandNewScript();", options).Ok);
        var script = Path.Combine(tree, "Scripts", "Game", "brandNewScript.gml");
        File.WriteAllText(script, "return 1;");
        // A directory's modification time has the file system's resolution; make
        // sure the addition lands in a later tick than the first walk saw.
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(script)!, DateTime.UtcNow.AddSeconds(1));
        Assert.True(env.Check("brandNewScript();", options).Ok);

        File.Delete(script);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(script)!, DateTime.UtcNow.AddSeconds(2));
        Assert.False(env.Check("brandNewScript();", options).Ok);
    }

    [Fact]
    public void Trees_in_a_different_order_are_the_same_project()
    {
        var other = _dir.Write("other/Scripts/fromOther.gml", "return 1;");
        var otherTree = Path.GetDirectoryName(Path.GetDirectoryName(other))!;
        var env = new LintEnvironment();
        var gm8 = Fixtures.FakeGm8(_dir);

        var a = env.Check("fromOther(); helper(1);", new LintOptions { Trees = [Fixtures.Tree, otherTree], Gm8Dir = gm8 });
        var b = env.Check("fromOther(); helper(1);", new LintOptions { Trees = [otherTree, Fixtures.Tree], Gm8Dir = gm8 });
        Assert.True(a.Ok);
        Assert.True(b.Ok);
        Assert.Same(
            env.Context([Fixtures.Tree, otherTree], new LintOptions(), gm8 + ""),
            env.Context([otherTree, Fixtures.Tree], new LintOptions(), gm8 + ""));
    }

    [Fact]
    public void The_server_echoes_ids_and_says_what_is_wrong_with_a_bad_request()
    {
        var env = new LintEnvironment();
        var defaults = new LintOptions { Trees = [Fixtures.Tree], Gm8Dir = Fixtures.FakeGm8(_dir) };

        var ok = LintServer.Answer("{\"id\": 7, \"code\": \"x = abs(1);\"}", env, defaults);
        Assert.True(ok.Ok);
        Assert.Equal(7, ok.Id!.Value.GetInt32());

        var bad = LintServer.Answer("{\"id\": \"q\", \"code\": \"x = (1;\"}", env, defaults);
        Assert.False(bad.Ok);
        Assert.Contains(bad.Errors, e => e.Rule == "unbalanced");
        Assert.Equal("q", bad.Id!.Value.GetString());

        Assert.Contains("\"code\" is required", LintServer.Answer("{\"id\": 1}", env, defaults).Error);
        Assert.Contains("not a JSON request", LintServer.Answer("not json", env, defaults).Error);
    }
}

public sealed class InstalledExtensionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>An installed package's .ged, reduced to what the linter reads: one file, its functions and constants.</summary>
    private static byte[] Ged(string package, string[] functions, string[] constants)
    {
        var w = new IO.ByteWriter();
        w.U32(700);
        w.U32(0);
        foreach (var s in new[] { package, "temp1", "1.0", "author", "date", "licence", "description", "" }) w.Str(s);
        w.U32(0); // hidden
        w.U32(0); // uses
        w.U32(1); // files
        w.U32(700);
        w.Str("ext.dll");
        w.Str("");
        w.U32(1);
        w.Str("");
        w.Str("");
        w.U32((uint)functions.Length);
        foreach (var f in functions)
        {
            w.U32(700);
            w.Str(f);
            w.Str(f);
            w.U32(12);
            w.Str(f + "()");
            w.U32(0);
            w.I32(0);
            for (var i = 0; i < 17; i++) w.U32(2);
            w.U32(2);
        }
        w.U32((uint)constants.Length);
        foreach (var c in constants)
        {
            w.U32(700);
            w.Str(c);
            w.Str("1");
            w.U32(0);
        }
        return w.ToArray();
    }

    [Fact]
    public void Functions_of_the_installed_packages_a_tree_uses_are_known()
    {
        var gm8 = Fixtures.FakeGm8(_dir);
        Directory.CreateDirectory(Path.Combine(gm8, "extensions"));
        File.WriteAllBytes(Path.Combine(gm8, "extensions", "Used.ged"), Ged("Used", ["used_send"], ["USED_PORT"]));
        File.WriteAllBytes(Path.Combine(gm8, "extensions", "Unused.ged"), Ged("Unused", ["unused_send"], []));

        var tree = Fixtures.CopyTree(_dir);
        File.WriteAllText(Path.Combine(tree, "Extension Packages.xml"),
            "<extensionPackages>\n  <package>Used</package>\n</extensionPackages>\n");

        var env = new LintEnvironment();
        var options = new LintOptions { Trees = [tree], Gm8Dir = gm8 };
        Assert.True(env.Check("used_send(USED_PORT);", options).Ok);
        Assert.False(env.Check("unused_send();", options).Ok);
    }
}

public sealed class ActionLibraryTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>A .lib holding one function-type action that calls `function`, and one code-type action.</summary>
    private static byte[] Lib(string function)
    {
        var w = new IO.ByteWriter();
        w.U32(520);
        w.Str("tab");
        w.U32(1);
        w.Str("author");
        w.U32(100);
        w.F64(0);
        w.Str("info");
        w.Str("init();");
        w.U32(0); // advanced
        w.U32(2); // next action id
        w.U32(2); // actions
        foreach (var (exec, fn) in new[] { (1u, function), (2u, "") })
        {
            w.U32(520);
            w.Str("action");
            w.U32(exec);
            w.Chunk([]); // image
            w.U32(0);
            w.U32(0);
            w.U32(0); // registered only
            w.Str("");
            w.Str("");
            w.Str("");
            w.U32(0); // kind
            w.U32(0); // interface
            w.U32(0);
            w.U32(1);
            w.U32(0);
            w.U32(1); // argument count
            w.U32(8);
            for (var a = 0; a < 8; a++)
            {
                w.Str("arg");
                w.U32(0);
                w.Str("0");
                w.Str("");
            }
            w.U32(exec);
            w.Str(fn);
            w.Str(exec == 2 ? "x = 1;" : "");
        }
        return w.ToArray();
    }

    [Fact]
    public void A_library_is_read_to_its_last_byte()
    {
        var path = _dir.Combine("test.lib");
        File.WriteAllBytes(path, Lib("action_do_thing"));
        var lib = Install.ActionLibrary.Read(path);
        Assert.Equal("init();", lib.InitCode);
        Assert.Equal(["action_do_thing"], lib.Functions);
    }

    [Fact]
    public void Functions_that_library_actions_call_are_built_ins()
    {
        var gm8 = Fixtures.FakeGm8(_dir);
        Directory.CreateDirectory(Path.Combine(gm8, "lib"));
        File.WriteAllBytes(Path.Combine(gm8, "lib", "01_test.lib"), Lib("action_do_thing"));
        var options = new LintOptions { Trees = [Fixtures.Tree], Gm8Dir = gm8 };
        Assert.True(new LintEnvironment().Check("action_do_thing(1);", options).Ok);
    }
}
