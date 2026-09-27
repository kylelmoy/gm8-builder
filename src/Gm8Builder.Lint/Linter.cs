using System.Text.RegularExpressions;

namespace Gm8Builder.Lint;

//=============================================================================
// A deterministic checker for Game Maker 8 GML.
//
// Exists because the only other way to know whether GML is valid is to compile
// it in Game Maker, and a GML error in a built exe - or in execute_string at
// runtime - is a modal dialog that freezes the game.
//
// It is authoritative rather than guesswork: GM8 ships a file called `fnames`
// listing every built-in name, 1069 of them with full signatures, e.g.
//     instance_create(x,y,obj)
//     execute_string(str,arg0,arg1,...)
// so both existence and argument counts are checked against the installed
// engine. Project symbols (scripts, objects, sprites, constants) are read from
// the split source tree.
//
// Deliberately conservative: it reports only what it is confident about. A
// linter that cries wolf gets ignored, and the compiler remains the final word.
//=============================================================================

public sealed record Finding(string Severity, string File, int Line, int Col, string Rule, string Message)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Severity == "error";
}

/// <summary>A built-in function's argument range, from its fnames signature.</summary>
public readonly record struct Arity(int Min, int Max);

/// <summary>What fnames says the engine knows.</summary>
public sealed class Builtins
{
    public Dictionary<string, Arity> Funcs { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Built-in *variables*, as opposed to functions and constants: fnames marks a
    /// constant with a trailing '#', and everything else that is not a call is a
    /// variable GM8 already owns. None can be redeclared with `var` - which is where
    /// "score", "lives", "health" and "room_speed" come from: names that read as
    /// ordinary locals and take a whole script's compile down.
    /// </summary>
    public HashSet<string> Vars { get; } = new(StringComparer.Ordinal);
}

/// <summary>Names the project defines, read from a split tree.</summary>
public sealed class ProjectSymbols
{
    /// <summary>Read-only at compile time: scripts, objects, sprites, sounds, backgrounds, rooms, fonts, timelines, paths, constants.</summary>
    public HashSet<string> Resources { get; } = new(StringComparer.Ordinal);

    /// <summary>Resources plus globalvars - everything a call or reference may legitimately name.</summary>
    public HashSet<string> All { get; } = new(StringComparer.Ordinal);
}

public sealed class LintContext
{
    public required Builtins Builtins { get; init; }
    public required ProjectSymbols Symbols { get; init; }
    public required HashSet<string> Extensions { get; init; }
    public bool Arity { get; init; } = true;
    public bool Style { get; init; }
}

public static partial class Linter
{
    private static readonly HashSet<string> Keywords =
    [
        "if", "else", "while", "do", "until", "for", "repeat", "switch", "case", "default",
        "break", "continue", "exit", "return", "with", "var", "globalvar", "then", "begin", "end",
        "and", "or", "not", "xor", "mod", "div", "true", "false",
    ];

    /// <summary>Identifiers GM8 accepts but later GameMaker versions reserved.</summary>
    private static readonly HashSet<string> FutureReserved =
        ["new", "delete", "function", "static", "constructor", "struct", "try", "catch", "finally", "throw"];

    /// <summary>GM8 accepts these, so they are style warnings, not errors.</summary>
    private static readonly Dictionary<string, string> StylePunct = new()
    {
        ["&&"] = "style: prefer \"and\" over \"&&\"",
        ["||"] = "style: prefer \"or\" over \"||\"",
        ["^^"] = "style: prefer \"xor\" over \"^^\"",
    };

    /// <summary>
    /// Functions that exist in modern GameMaker but not in GM8. Calling one is a
    /// hard error, and the single most likely mistake from anyone - or anything -
    /// that learned GML from GameMaker Studio.
    /// </summary>
    private static readonly HashSet<string> ModernOnly =
    [
        "array_length", "array_push", "array_pop", "array_insert", "array_delete", "array_sort",
        "array_create", "array_copy", "array_resize", "array_get", "array_set",
        "string_split", "string_join", "string_trim", "string_starts_with", "string_ends_with",
        "struct_exists", "struct_get", "struct_set", "struct_remove", "variable_struct_exists",
        "method", "method_call", "is_method", "is_struct", "is_undefined", "is_array", "is_ptr",
        "json_parse", "json_stringify", "buffer_write", "buffer_read",
        "instance_create_layer", "instance_create_depth", "layer_create", "draw_sprite_pos",
        "audio_play_sound", "audio_stop_sound",
        "ds_map_secure_save", "string_hash_to_newline", "gc_collect", "exception_unhandled_handler",
    ];

    /// <summary>
    /// Built-in instance variables. `var` declaring a local of the same name is not
    /// a redeclaration - it is a compilation error that takes the whole script's
    /// compile down with it (GM8 compiles every script at load, before any Create
    /// event runs), which is what makes it so much more expensive than the message
    /// suggests.
    /// </summary>
    private static readonly HashSet<string> InstanceVars =
    [
        "x", "y", "xprevious", "yprevious", "xstart", "ystart",
        "hspeed", "vspeed", "speed", "direction", "friction", "gravity", "gravity_direction",
        "solid", "persistent", "depth", "visible", "id",
        "object_index", "sprite_index", "mask_index",
        "image_index", "image_speed", "image_xscale", "image_yscale", "image_angle", "image_alpha", "image_blend",
        "alarm", "bbox_left", "bbox_right", "bbox_top", "bbox_bottom",
    ];

    /// <summary>Assignment operators. Excludes "==", "&lt;=", "&gt;=" and "!=" - none write anything.</summary>
    private static readonly HashSet<string> AssignOps = ["=", "+=", "-=", "*=", "/=", "|=", "&=", "^=", ":="];

    /// <summary>
    /// Every punctuation operator that must be followed by the start of an operand.
    /// Excludes ".", ":" and "?": "." wants specifically an identifier, "?" is banned
    /// outright, and ":" is mostly a case terminator, not an operator.
    /// </summary>
    private static readonly HashSet<string> NeedsRightOperandPunct =
    [
        "+", "-", "*", "/", "<<", ">>", "<", "<=", ">", ">=", "==", "!=",
        "&", "|", "^", "&&", "||", "^^",
        "=", "+=", "-=", "*=", "/=", "|=", "&=", "^=", ":=",
        "!", "~",
    ];

    private static readonly HashSet<string> NeedsRightOperandWord = ["and", "or", "xor", "div", "mod", "not"];

    /// <summary>
    /// Identifiers that cannot begin an operand: the binary word-operators (they
    /// need a left operand first) and every statement-level keyword.
    /// </summary>
    private static readonly HashSet<string> NotAnOperandStart =
    [
        "and", "or", "xor", "div", "mod",
        "if", "else", "while", "do", "until", "for", "repeat", "switch", "case", "default",
        "break", "continue", "exit", "return", "with", "var", "globalvar", "then", "begin", "end",
    ];

    private static bool StartsOperand(Token? t)
    {
        if (t is not { } tok) return false;
        return tok.Type switch
        {
            TokenType.Number or TokenType.String => true,
            TokenType.Punct => tok.Value is "(" or "-" or "+" or "~" or "!",
            TokenType.Ident => !NotAnOperandStart.Contains(tok.Value),
            _ => false,
        };
    }

    private static bool IsPunct(Token t, string value) => t.Type == TokenType.Punct && t.Value == value;

    public static List<Finding> LintSource(string src, LintContext ctx, string originName)
    {
        var findings = new List<Finding>();
        void Add(string sev, int line, int col, string rule, string msg) =>
            findings.Add(new Finding(sev, originName, line, col, rule, msg));

        var toks = Tokenizer.Tokenize(src);
        Token? At(int k) => k >= 0 && k < toks.Count ? toks[k] : null;

        // --- lexical problems ---
        foreach (var t in toks)
        {
            if (t.Type == TokenType.UnterminatedString) Add("error", t.Line, t.Col, "unterminated-string", "string literal is never closed");
            if (t.Type == TokenType.UnterminatedComment) Add("error", t.Line, t.Col, "unterminated-comment", "block comment is never closed");
        }

        // --- HTML-escaped operators ---
        //
        // `&lt;`, `&gt;` and `&amp;` cannot be intentional in GML, but unescaped they
        // tokenize as innocuous-looking sequences ("&", "gt", ";") that pass every
        // check below while producing code GM8 cannot compile. By the time these
        // are tokens the fact that they were ever one entity is gone, so this is a
        // plain scan over the source instead.
        foreach (Match m in HtmlEntity().Matches(src))
        {
            var line = 1;
            var lastNewline = -1;
            for (var k = 0; k < m.Index; k++)
            {
                if (src[k] != '\n') continue;
                line++;
                lastNewline = k;
            }
            var ch = m.Groups[1].Value switch { "lt" => "<", "gt" => ">", _ => "&" };
            Add("error", line, m.Index - lastNewline, "html-entity",
                $"\"&{m.Groups[1].Value};\" is an HTML-escaped \"{ch}\" - GM8 will not parse it as one; send a literal \"{ch}\" instead");
        }

        // --- style: symbol operators, and "!" as logical not ---
        if (ctx.Style)
        {
            foreach (var t in toks)
            {
                if (t.Type == TokenType.Punct && StylePunct.TryGetValue(t.Value, out var msg)) Add("warning", t.Line, t.Col, "style-operator", msg);
            }
            for (var k = 0; k < toks.Count; k++)
            {
                if (IsPunct(toks[k], "!") && At(k + 1)?.Value != "=")
                    Add("warning", toks[k].Line, toks[k].Col, "style-operator", "style: prefer \"not\" over \"!\"");
            }
        }

        // --- reserved-word hazards ---
        foreach (var t in toks)
        {
            if (t.Type == TokenType.Ident && FutureReserved.Contains(t.Value))
                Add("error", t.Line, t.Col, "future-reserved",
                    $"\"{t.Value}\" is not usable here: GM8 has no such construct, and later GameMaker versions reserve the word");
        }

        // --- operator with no right operand ---
        //
        // A binary or unary operator always needs an operand immediately after it.
        // This checks only what comes right after the operator, not full expression
        // well-formedness: an operator is never directly followed by a closing
        // bracket, a separator, or another operand-hungry operator, all of which the
        // compiler chokes on at exactly that spot.
        for (var k = 0; k < toks.Count; k++)
        {
            var t = toks[k];
            var isOperator = (t.Type == TokenType.Punct && NeedsRightOperandPunct.Contains(t.Value)) ||
                             (t.Type == TokenType.Ident && NeedsRightOperandWord.Contains(t.Value));
            if (!isOperator) continue;
            var next = At(k + 1);
            if (!StartsOperand(next))
            {
                Add("error", t.Line, t.Col, "dangling-operator", next is { } n
                    ? $"\"{t.Value}\" has no right operand - the next token, \"{n.Value}\", cannot start an expression here"
                    : $"\"{t.Value}\" has no right operand - nothing follows it");
            }
        }

        // --- ";" inside a parenthesized expression, and "," inside a grouping "(" ---
        //
        // `for (init; cond; incr)` is the one legitimate place a top-level ";"
        // appears between "(" and its ")". Each "(" is marked when it opens by
        // whether "for" was just before it, and whether it looks like a call - an
        // identifier that is not a keyword, or a closing ")"/"]", immediately to its
        // left. "[" and "{" are pushed too, only so nesting stays right: in
        // `(a[i, j])` the comma belongs to the 2D index, not the grouping paren.
        // GM8 has no comma operator, so a "," whose nearest enclosing bracket is a
        // grouping "(" cannot compile.
        var parenStack = new List<(string Type, bool ForLoop, bool IsCall)>();
        for (var k = 0; k < toks.Count; k++)
        {
            var t = toks[k];
            if (t.Type != TokenType.Punct) continue;
            switch (t.Value)
            {
                case "(":
                {
                    var prev = At(k - 1);
                    var isCall = prev is { } p &&
                                 ((p.Type == TokenType.Ident && !Keywords.Contains(p.Value)) ||
                                  (p.Type == TokenType.Punct && p.Value is ")" or "]"));
                    var forLoop = prev is { Type: TokenType.Ident, Value: "for" };
                    parenStack.Add(("(", forLoop, isCall));
                    break;
                }
                case "[" or "{":
                    parenStack.Add((t.Value, false, false));
                    break;
                case ")" or "]" or "}":
                    if (parenStack.Count > 0) parenStack.RemoveAt(parenStack.Count - 1);
                    break;
                case ";":
                    if (parenStack.Count > 0 && parenStack[^1] is { Type: "(", ForLoop: false })
                        Add("error", t.Line, t.Col, "semicolon-in-expression",
                            "\";\" is a statement separator - it cannot appear inside a parenthesized expression or call " +
                            "(only a for-loop's own parentheses may contain one)");
                    break;
                case ",":
                    if (parenStack.Count > 0 && parenStack[^1] is { Type: "(", ForLoop: false, IsCall: false })
                        Add("error", t.Line, t.Col, "comma-in-grouping",
                            "\",\" has no meaning inside a grouping \"(...)\" - GM8 has no comma operator " +
                            "(only a call's argument list, or \"var i, j;\", may contain one)");
                    break;
            }
        }

        // --- ternary ---
        foreach (var t in toks)
        {
            if (IsPunct(t, "?")) Add("error", t.Line, t.Col, "ternary", "GM8 has no ternary operator; use if/else");
        }

        // --- bracket balance ---
        var stack = new List<Token>();
        foreach (var t in toks)
        {
            if (t.Type != TokenType.Punct) continue;
            if (t.Value is "(" or "[" or "{")
            {
                stack.Add(t);
                continue;
            }
            var opener = t.Value switch { ")" => "(", "]" => "[", "}" => "{", _ => null };
            if (opener == null) continue;
            if (stack.Count == 0)
            {
                Add("error", t.Line, t.Col, "unbalanced", $"unmatched \"{t.Value}\"");
                continue;
            }
            var open = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (open.Value != opener) Add("error", t.Line, t.Col, "unbalanced", $"\"{t.Value}\" closes \"{open.Value}\" opened at line {open.Line}");
        }
        foreach (var open in stack) Add("error", open.Line, open.Col, "unbalanced", $"\"{open.Value}\" is never closed");

        // --- var: locals, and locals that shadow a built-in ---
        var localVars = new HashSet<string>(StringComparer.Ordinal);
        for (var k = 0; k < toks.Count; k++)
        {
            var t = toks[k];
            if (t.Type != TokenType.Ident || t.Value is not ("var" or "globalvar")) continue;
            var isVar = t.Value == "var";
            for (var j = k + 1; j < toks.Count && toks[j].Value != ";"; j++)
            {
                if (toks[j].Type != TokenType.Ident) continue;
                var name = toks[j].Value;
                localVars.Add(name);
                if (isVar && (InstanceVars.Contains(name) || ctx.Builtins.Vars.Contains(name)))
                {
                    var kind = InstanceVars.Contains(name) ? "instance" : "global";
                    Add("error", toks[j].Line, toks[j].Col, "var-shadows-builtin",
                        $"\"var {name}\" is a compilation error in GM8: \"{name}\" is a built-in {kind} " +
                        "variable, not a free name - the whole script fails to compile, before any Create event runs");
                }
            }
        }

        // --- assignment to a resource or constant name ---
        //
        // Resource and constant names are read-only constants at compile time, so
        // `global.foo = -1;` is not an assignment when a script called foo exists -
        // it fails to compile, and just as silently as the var-shadow case.
        for (var k = 0; k < toks.Count; k++)
        {
            var t = toks[k];
            if (t.Type != TokenType.Ident || At(k + 1) is not { Type: TokenType.Punct } next || !AssignOps.Contains(next.Value)) continue;
            if (!ctx.Symbols.Resources.Contains(t.Value)) continue;
            Add("error", t.Line, t.Col, "assign-to-resource-name",
                $"\"{t.Value}\" is a script/object/sprite/room/constant name, which is a read-only constant at compile " +
                "time - assigning to it is a compilation error, not an assignment");
        }

        // --- calls: existence and arity ---
        for (var k = 0; k < toks.Count; k++)
        {
            var t = toks[k];
            if (t.Type != TokenType.Ident || At(k + 1)?.Value != "(") continue;
            if (Keywords.Contains(t.Value)) continue;
            // a.b(...) is a method-ish access; GM8 has none, but skip to avoid noise
            if (At(k - 1) is { } before && IsPunct(before, ".")) continue;

            // fnames is authoritative, so never contradict it: a wrong entry in
            // ModernOnly degrades to silence rather than a false positive.
            if (ModernOnly.Contains(t.Value) && !ctx.Builtins.Names.Contains(t.Value))
            {
                Add("error", t.Line, t.Col, "modern-function", $"\"{t.Value}\" is a modern GameMaker function and does not exist in GM8");
                continue;
            }

            var hasBuiltin = ctx.Builtins.Funcs.TryGetValue(t.Value, out var arity);
            var known = hasBuiltin || ctx.Builtins.Names.Contains(t.Value) || ctx.Symbols.All.Contains(t.Value) ||
                        ctx.Extensions.Contains(t.Value) || localVars.Contains(t.Value);
            if (!known)
            {
                Add("error", t.Line, t.Col, "unknown-function",
                    $"\"{t.Value}\" is not a GM8 built-in, a project script, or a known extension function" +
                    " (extension functions come from the installed packages in Extension Packages.xml," +
                    " or a file passed with --extensions)");
                continue;
            }

            if (hasBuiltin && ctx.Arity)
            {
                var count = CountArguments(toks, k + 1);
                if (count < arity.Min || count > arity.Max)
                {
                    var want = arity.Max == int.MaxValue ? $"{arity.Min}+" : arity.Min.ToString();
                    Add("error", t.Line, t.Col, "arity", $"{t.Value}() takes {want} argument{(want == "1" ? "" : "s")}, got {count}");
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// Top-level arguments between the "(" at `open` and its match. A bracket that
    /// opens directly inside the call - `abs((y))`, `f([1])` - starts an argument
    /// like any other token; the first implementation missed that and counted such
    /// a call as having none.
    /// </summary>
    private static int CountArguments(List<Token> toks, int open)
    {
        int depth = 0, commas = 0;
        var seenAny = false;
        for (var j = open; j < toks.Count; j++)
        {
            var t = toks[j];
            if (t.Type == TokenType.Punct && t.Value is "(" or "[" or "{")
            {
                depth++;
                if (depth == 1) continue;
                if (depth == 2) seenAny = true;
                continue;
            }
            if (t.Type == TokenType.Punct && t.Value is ")" or "]" or "}")
            {
                depth--;
                if (depth == 0) break;
                continue;
            }
            if (depth != 1) continue;
            if (IsPunct(t, ",")) commas++;
            else seenAny = true;
        }
        return seenAny || commas > 0 ? commas + 1 : 0;
    }

    /// <summary>
    /// Event XML: GML lives inside &lt;argument kind="STRING"&gt;, XML-escaped. Reported
    /// lines point into the XML file, not into the snippet.
    /// </summary>
    public static List<Finding> LintEventXml(string xml, LintContext ctx, string originName)
    {
        var findings = new List<Finding>();
        foreach (Match m in EventArgument().Matches(xml))
        {
            var payloadStart = m.Index + m.Value.IndexOf('>') + 1;
            var baseLine = 0;
            for (var k = 0; k < payloadStart; k++)
            {
                if (xml[k] == '\n') baseLine++;
            }
            var gml = UnescapeXml(m.Groups[1].Value);
            foreach (var f in LintSource(gml, ctx, originName)) findings.Add(f with { Line = f.Line + baseLine });
        }
        return findings;
    }

    private static string UnescapeXml(string s)
    {
        s = s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'");
        s = DecimalRef().Replace(s, m => ((char)int.Parse(m.Groups[1].Value)).ToString());
        s = HexRef().Replace(s, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        return s.Replace("&amp;", "&");
    }

    [GeneratedRegex("&#([0-9]+);")]
    private static partial Regex DecimalRef();

    [GeneratedRegex("&#x([0-9a-fA-F]+);")]
    private static partial Regex HexRef();

    [GeneratedRegex("&(lt|gt|amp);")]
    private static partial Regex HtmlEntity();

    [GeneratedRegex("<argument kind=\"STRING\">([\\s\\S]*?)</argument>")]
    private static partial Regex EventArgument();
}
