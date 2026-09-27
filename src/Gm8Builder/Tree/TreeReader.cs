using System.Text.RegularExpressions;
using Gm8Builder.Images;
using Gm8Builder.Model;
using Gm8Builder.Pe;
using Action = Gm8Builder.Model.Action;

namespace Gm8Builder.Tree;

//=============================================================================
// A GmkSplitter tree, read straight into the form the runner loads.
//
// The reference is gmksplit's compose direction (tree -> .gmk) followed by Game Maker's Create Executable. Where the two disagree
// with a plain reading of the XML, the comment says which one decided.
//
// Indices: every resource type is numbered in tree order (depth-first through
// each _resources.list.xml), except objects, which keep the id="" in their
// XML; gmksplit's OrderPreservingDupeRemoval resolves clashes and gaps become
// deleted slots. Instance and tile ids are always renumbered, from 100001 and
// 10000001, in room order.
//=============================================================================

/// <summary>A tree, read: the game data, and what goes around it in the executable.</summary>
public sealed record Project(
    GameData Data, Settings Settings, VersionInfo Version, byte[]? Icon,
    IReadOnlyList<string> ExtensionNames, IReadOnlyList<string> Warnings);

public sealed partial class TreeReader
{
    private readonly string _root;
    private readonly List<string> _warnings = [];

    private sealed record Entry(string Name, string Dir, string File);

    private sealed class Kind(string folder)
    {
        public string Folder { get; } = folder;
        public List<Entry> Entries { get; } = [];
        /// <summary>Entry at each index; null for a gap.</summary>
        public List<Entry?> Slots { get; } = [];
        public Dictionary<string, int> Index { get; } = new(StringComparer.Ordinal);
    }

    private readonly Kind _sprites = new("Sprites");
    private readonly Kind _sounds = new("Sounds");
    private readonly Kind _backgrounds = new("Backgrounds");
    private readonly Kind _paths = new("Paths");
    private readonly Kind _scripts = new("Scripts");
    private readonly Kind _fonts = new("Fonts");
    private readonly Kind _timelines = new("Time Lines");
    private readonly Kind _objects = new("Objects");
    private readonly Kind _rooms = new("Rooms");

    private TreeReader(string root) => _root = root;

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Everything a tree defines. The extensions it names and the library init code come from elsewhere.</summary>
    public static Project Read(string root)
    {
        var r = new TreeReader(root);
        var (data, settings) = r.ReadAll();
        var icon = TreePath.Combine(root, "game icon.ico");
        return new Project(data, settings, r.ReadVersion(), File.Exists(icon) ? File.ReadAllBytes(icon) : null,
            r.ReadExtensionNames(), r.Warnings);
    }

    private (GameData, Settings) ReadAll()
    {
        Kind[] kinds = [_sprites, _sounds, _backgrounds, _paths, _scripts, _fonts, _timelines, _objects, _rooms];
        foreach (var k in kinds)
        {
            var dir = TreePath.Combine(_root, k.Folder);
            if (Directory.Exists(dir)) Walk(k, dir);
        }
        foreach (var k in kinds) Number(k);

        var g = new GameData { Pro = true };
        g.Sprites = Load(_sprites, ReadSprite);
        g.Sounds = Load(_sounds, ReadSound);
        g.Backgrounds = Load(_backgrounds, ReadBackground);
        g.Paths = Load(_paths, ReadPath);
        g.Scripts = Load(_scripts, ReadScript);
        g.Fonts = Load<Font>(_fonts, (e, _) => throw new NotSupportedException($"font {e.Name}: fonts are not supported yet"));
        g.Timelines = Load(_timelines, ReadTimeline);
        g.Objects = Load(_objects, ReadObject);
        g.Rooms = Load(_rooms, ReadRoom);
        g.RoomOrder = _rooms.Entries.Select(e => _rooms.Index[e.Name]).ToList();
        NumberInstancesAndTiles(g);

        g.Constants = ReadConstants();
        g.IncludedFiles = ReadIncludedFiles();
        g.Information = ReadGameInformation();
        var settings = ReadSettings(g);
        return (g, settings);
    }

    private void Warn(string message) => _warnings.Add(message);

    //---------------------------------------------------------------------------
    // The resource tree and numbering
    //---------------------------------------------------------------------------

    private static void Walk(Kind k, string dir)
    {
        var list = TreePath.Combine(dir, "_resources.list.xml");
        if (!File.Exists(list)) return; // gmksplit warns and skips the directory
        foreach (var res in Xml.Load(list).Expect("resources").Children("resource"))
        {
            var name = res.Attr("name");
            var file = res.HasAttr("filename") && res.Attr("filename") != "" ? res.Attr("filename") : name;
            if (res.Attr("type") == "GROUP") Walk(k, TreePath.Combine(dir, file));
            else k.Entries.Add(new Entry(name, dir, file));
        }
    }

    /// <summary>gmksplit's OrderPreservingDupeRemoval over resource ids.</summary>
    private void Number(Kind k)
    {
        var ids = new List<(Entry Entry, int? Id)>();
        foreach (var e in k.Entries)
        {
            int? id = null;
            if (k == _objects)
            {
                var x = Xml.Load(XmlPath(e));
                if (x.HasAttr("id") && x.AttrInt("id") >= 0) id = x.AttrInt("id");
            }
            ids.Add((e, id));
        }

        var assigned = new Dictionary<Entry, int>();
        var next = 0;
        foreach (var (e, id) in ids.Where(x => x.Id != null).OrderBy(x => x.Id!.Value)) // stable, as TreeMultiMap is
        {
            if (id!.Value >= next)
            {
                assigned[e] = id.Value;
                next = id.Value + 1;
            }
            else assigned[e] = next++;
        }
        foreach (var (e, _) in ids.Where(x => x.Id == null)) assigned[e] = next++;

        foreach (var e in k.Entries)
        {
            if (!k.Index.TryAdd(e.Name, assigned[e]))
                throw new InvalidDataException($"{k.Folder}: two resources named {e.Name}");
        }
        var count = assigned.Count == 0 ? 0 : assigned.Values.Max() + 1;
        for (var i = 0; i < count; i++) k.Slots.Add(null);
        foreach (var (e, i) in assigned) k.Slots[i] = e;
    }

    private static List<T?> Load<T>(Kind k, Func<Entry, Kind, T> read) where T : class
    {
        var result = new T?[k.Slots.Count];
        IO.Parallelism.For(k.Slots.Count, i =>
        {
            if (k.Slots[i] is { } e) result[i] = read(e, k);
        });
        return [.. result];
    }

    private static string XmlPath(Entry e) => TreePath.Combine(e.Dir, e.File + ".xml");

    private int Ref(Kind k, string name, string context)
    {
        if (name == "") return -1;
        if (k.Index.TryGetValue(name, out var i)) return i;
        lock (_warnings) Warn($"{context}: unknown {k.Folder} \"{name}\"");
        return -1;
    }

    /// <summary>Hex RRGGBB, stored by Game Maker as 0x00BBGGRR.</summary>
    private static uint Colour(string hex)
    {
        var v = Convert.ToUInt32(hex.Trim(), 16);
        if (hex.Trim().Length == 8) v &= 0xFFFFFF;
        return ((v & 0xFF) << 16) | (v & 0xFF00) | ((v >> 16) & 0xFF);
    }

    //---------------------------------------------------------------------------
    // Resources
    //---------------------------------------------------------------------------

    private Sprite ReadSprite(Entry e, Kind k)
    {
        var x = Xml.Load(XmlPath(e)).Expect("sprite");
        var origin = x.Child("origin");
        var mask = x.Child("mask");
        var bounds = mask.Child("bounds");
        var def = new SpriteDefinition
        {
            Separate = mask.Bool("separate"),
            Shape = Enum.Parse<MaskShape>(mask.Str("shape")),
            BoundsMode = Enum.Parse<BoundsMode>(bounds.Attr("mode")),
            AlphaTolerance = bounds.AttrInt("alphaTolerance"),
            Transparent = x.Bool("transparent"),
            SmoothEdges = x.Bool("smoothEdges"),
        };
        if (def.BoundsMode == BoundsMode.MANUAL)
        {
            def.Left = bounds.Int("left");
            def.Right = bounds.Int("right");
            def.Top = bounds.Int("top");
            def.Bottom = bounds.Int("bottom");
        }

        var images = new List<Rgba>();
        var dir = TreePath.Combine(e.Dir, e.File + ".images");
        for (var i = 0; File.Exists(TreePath.Combine(dir, $"image {i}.png")); i++)
            images.Add(Png.Read(TreePath.Combine(dir, $"image {i}.png")));

        return SpriteBuilder.Build(GmText.Store(e.Name), origin.AttrInt("x"), origin.AttrInt("y"), images, def);
    }

    private Sound ReadSound(Entry e, Kind k)
    {
        var x = Xml.Load(XmlPath(e)).Expect("sound");
        var fx = x.Child("effects");
        var s = new Sound
        {
            Name = GmText.Store(e.Name),
            FileName = GmText.Store(x.Str("filename")),
            FileType = GmText.Store(x.Str("filetype")),
            Kind = x.Str("kind") switch
            {
                "NORMAL" => 0u,
                "BACKGROUND" => 1u,
                "SPATIAL" => 2u,
                "MULTIMEDIA" => 3u,
                var other => throw new InvalidDataException($"{XmlPath(e)}: sound kind {other}"),
            },
            Pan = x.Double("pan"),
            Volume = x.Double("volume"),
            Preload = x.Bool("preload"),
            Effects = (fx.Bool("chorus") ? 1u : 0) | (fx.Bool("echo") ? 2u : 0) | (fx.Bool("flanger") ? 4u : 0)
                | (fx.Bool("gargle") ? 8u : 0) | (fx.Bool("reverb") ? 16u : 0),
        };

        // gmksplit names the data file after the sound, with its file type as the extension.
        var type = x.Str("filetype");
        var data = type.StartsWith('.') ? TreePath.Combine(e.Dir, e.File + type) : TreePath.Combine(e.Dir, e.File);
        if (File.Exists(data)) s.Data = File.ReadAllBytes(data);
        return s;
    }

    private Background ReadBackground(Entry e, Kind k)
    {
        var x = Xml.Load(XmlPath(e)).Expect("background");
        var b = new Background { Name = GmText.Store(e.Name) };
        var png = TreePath.Combine(e.Dir, e.File + ".png");
        if (File.Exists(png))
        {
            var img = Png.Read(png);
            b.Width = (uint)img.Width;
            b.Height = (uint)img.Height;
            b.Pixels = SpriteBuilder.Pixels(img, x.Bool("transparent"), x.Bool("smoothEdges"));
        }
        return b;
    }

    private GamePath ReadPath(Entry e, Kind k)
    {
        var x = Xml.Load(XmlPath(e)).Expect("path");
        return new GamePath
        {
            Name = GmText.Store(e.Name),
            Kind = x.Bool("smooth") ? 1u : 0u,
            Closed = x.Bool("closed"),
            Precision = (uint)x.Int("precision"),
            Points = x.Child("points").Children("point")
                .Select(p => new PathPoint(p.AttrInt("x"), p.AttrInt("y"), p.AttrInt("speed"))).ToList(),
        };
    }

    [GeneratedRegex(@"/\* !scriptId=(\d+) \*/\r?\n")]
    private static partial Regex ScriptIdComment();

    private Script ReadScript(Entry e, Kind k)
    {
        var code = GmText.ReadFile(TreePath.Combine(e.Dir, e.File + ".gml"));
        var m = ScriptIdComment().Match(code);
        if (m.Success) code = code.Remove(m.Index, m.Length);
        return new Script { Name = GmText.Store(e.Name), Code = code };
    }

    private Timeline ReadTimeline(Entry e, Kind k)
    {
        var x = Xml.Load(XmlPath(e)).Expect("timeline");
        var t = new Timeline { Name = GmText.Store(e.Name) };
        foreach (var m in x.Children("moment"))
            t.Moments.Add(((uint)m.AttrInt("stepNo"), m.Children("action").Select(a => ReadAction(a, $"timeline {e.Name}")).ToList()));
        return t;
    }

    private GameObject ReadObject(Entry e, Kind k)
    {
        var x = Xml.Load(XmlPath(e)).Expect("object");
        var context = $"object {e.Name}";
        var o = new GameObject
        {
            Name = GmText.Store(e.Name),
            Sprite = Ref(_sprites, x.Str("sprite"), context),
            Solid = x.Bool("solid"),
            Visible = x.Bool("visible"),
            Depth = x.Int("depth"),
            Persistent = x.Bool("persistent"),
            Parent = Ref(_objects, x.Str("parent"), context) is var parent and >= 0 ? parent : -100,
            Mask = Ref(_sprites, x.Str("mask"), context),
        };

        var dir = TreePath.Combine(e.Dir, e.File + ".events");
        if (!Directory.Exists(dir)) return o;
        foreach (var file in TreePath.Files(dir, "*.xml"))
        {
            var ev = Xml.Load(file).Expect("event");
            var type = Array.IndexOf(EventCategories, ev.Attr("category"));
            if (type < 0) throw new InvalidDataException($"{file}: unknown event category {ev.Attr("category")}");
            var sub = type == 4 ? Ref(_objects, ev.Attr("with"), $"{context} collision event") : ev.AttrInt("id");
            var actions = ev.Child("actions").Children("action").Select(a => ReadAction(a, context)).ToList();
            o.Events[type].Add(((uint)sub, actions));
        }
        // Game Maker writes each event list highest sub-event first.
        for (var i = 0; i < o.Events.Length; i++) o.Events[i] = o.Events[i].OrderByDescending(ev => ev.Sub).ToList();
        return o;
    }

    private static readonly string[] EventCategories =
        ["CREATE", "DESTROY", "ALARM", "STEP", "COLLISION", "KEYBOARD", "MOUSE", "OTHER", "DRAW", "KEYPRESS", "KEYRELEASE", "TRIGGER"];

    private static readonly string[] ActionKinds =
        ["NORMAL", "BEGIN", "END", "ELSE", "EXIT", "REPEAT", "VARIABLE", "CODE", "PLACEHOLDER", "SEPARATOR", "LABEL"];

    private static readonly string[] ExecTypes = ["NONE", "FUNCTION", "CODE"];

    private static readonly string[] ArgumentKinds =
        ["EXPRESSION", "STRING", "BOTH", "BOOLEAN", "MENU", "SPRITE", "SOUND", "BACKGROUND", "PATH", "SCRIPT", "GMOBJECT", "ROOM", "FONT", "COLOR", "TIMELINE", "FONTSTRING"];

    private Action ReadAction(Xml x, string context)
    {
        x.Expect("action");
        var exec = Lookup(ExecTypes, x.Str("actionType"), x, "actionType");
        var info = GmText.Store(x.Str("functionName"));
        var a = new Action
        {
            LibraryId = (uint)x.AttrInt("library"),
            ActionId = (uint)x.AttrInt("id"),
            Kind = (uint)Lookup(ActionKinds, x.Str("kind"), x, "kind"),
            MayBeRelative = x.Bool("allowRelative"),
            IsQuestion = x.Bool("question"),
            AppliesToSomething = x.Bool("canApplyTo"),
            ExecutionType = (uint)exec,
            FunctionName = exec == 1 ? info : "",
            Code = exec == 2 ? info : "",
            Relative = x.Bool("relative"),
            Not = x.Bool("not"),
        };

        var applies = x.Str("appliesTo");
        a.AppliesTo = applies.ToLowerInvariant() switch
        {
            ".self" => -1,
            ".other" => -2,
            "" => -1,
            _ => Ref(_objects, applies, context) is var i and >= 0 ? i : -1,
        };

        var args = x.Child("arguments").Children("argument").ToList();
        if (args.Count > Action.MaxArgs) throw new InvalidDataException($"{x.File}: an action with {args.Count} arguments");
        a.ArgCount = (uint)args.Count;
        for (var i = 0; i < args.Count; i++)
        {
            var kind = Lookup(ArgumentKinds, args[i].Attr("kind"), x, "argument kind");
            a.ArgKinds[i] = (uint)kind;
            var text = args[i].Text;
            a.Args[i] = ArgumentResource(kind) is { } res
                ? Ref(res, text, context).ToString()
                : GmText.Store(exec == 2 && kind == 1 ? GmText.ToCrlf(text) : text);
        }
        return a;
    }

    private Kind? ArgumentResource(int kind) => kind switch
    {
        5 => _sprites,
        6 => _sounds,
        7 => _backgrounds,
        8 => _paths,
        9 => _scripts,
        10 => _objects,
        11 => _rooms,
        12 => _fonts,
        14 => _timelines,
        _ => null,
    };

    private static int Lookup(string[] names, string value, Xml x, string what)
    {
        var i = Array.IndexOf(names, value.Trim());
        return i >= 0 ? i : throw new InvalidDataException($"{x.File}: unknown {what} {value}");
    }

    private Room ReadRoom(Entry e, Kind k)
    {
        var x = Xml.Load(XmlPath(e)).Expect("room");
        var context = $"room {e.Name}";
        var size = x.Child("size");
        var r = new Room
        {
            Name = GmText.Store(e.Name),
            Caption = GmText.Store(x.Str("caption")),
            Width = (uint)size.AttrInt("width"),
            Height = (uint)size.AttrInt("height"),
            Speed = (uint)x.Int("speed"),
            Persistent = x.Bool("persistent"),
            CreationCode = GmText.Store(GmText.ToCrlf(x.Str("creationCode"))),
            Colour = Colour(x.Str("backgroundColor")),
            ClearScreen = x.Bool("drawBackgroundColor"),
            ViewsEnabled = x.Bool("enableViews"),
        };

        var defs = x.Child("backgrounds").Children("backgroundDef").ToList();
        for (var i = 0; i < 8; i++)
        {
            var b = new RoomBackground();
            if (i < defs.Count)
            {
                var d = defs[i];
                b.Visible = d.Bool("visibleOnRoomStart");
                b.Foreground = d.Bool("isForeground");
                b.Background = Ref(_backgrounds, d.Str("backgroundImage"), context);
                b.X = d.Child("offset").AttrInt("x");
                b.Y = d.Child("offset").AttrInt("y");
                b.HSpeed = d.Child("speed").AttrInt("x");
                b.VSpeed = d.Child("speed").AttrInt("y");
                b.TileH = d.Bool("tileHorizontally");
                b.TileV = d.Bool("tileVertically");
                b.Stretch = d.Bool("stretch");
            }
            else
            {
                b.TileH = b.TileV = true;
            }
            r.Backgrounds.Add(b);
        }

        var views = r.ViewsEnabled ? x.Child("views").Children("view").ToList() : [];
        for (var i = 0; i < 8; i++)
        {
            var v = new View { ViewW = 640, ViewH = 480, PortW = 640, PortH = 480, HBorder = 32, VBorder = 32, HSpeed = -1, VSpeed = -1 };
            if (i < views.Count)
            {
                var d = views[i];
                v.Visible = d.Bool("visibleOnRoomStart");
                var vr = d.Child("viewInRoom");
                (v.ViewX, v.ViewY, v.ViewW, v.ViewH) = (vr.AttrInt("x"), vr.AttrInt("y"), (uint)vr.AttrInt("width"), (uint)vr.AttrInt("height"));
                var p = d.Child("portOnScreen");
                (v.PortX, v.PortY, v.PortW, v.PortH) = (p.AttrInt("x"), p.AttrInt("y"), (uint)p.AttrInt("width"), (uint)p.AttrInt("height"));
                var f = d.Child("objectFollowing");
                v.Follow = Ref(_objects, f.Text, context);
                (v.HBorder, v.VBorder, v.HSpeed, v.VSpeed) = (f.AttrInt("hBorder"), f.AttrInt("vBorder"), f.AttrInt("hSpeed"), f.AttrInt("vSpeed"));
            }
            r.Views.Add(v);
        }

        foreach (var i in x.Child("instances").Children("instance"))
        {
            var pos = i.Child("position");
            r.Instances.Add(new Instance
            {
                X = pos.AttrInt("x"),
                Y = pos.AttrInt("y"),
                Object = Ref(_objects, i.Str("object"), context),
                CreationCode = GmText.Store(GmText.ToCrlf(i.Str("creationCode"))),
            });
        }

        foreach (var t in x.Child("tiles").Children("tile"))
        {
            var bp = t.Child("backgroundPosition");
            var rp = t.Child("roomPosition");
            var sz = t.Child("size");
            r.Tiles.Add(new Tile
            {
                Background = Ref(_backgrounds, t.Str("background"), context),
                TileX = bp.AttrInt("x"),
                TileY = bp.AttrInt("y"),
                X = rp.AttrInt("x"),
                Y = rp.AttrInt("y"),
                Width = sz.AttrInt("width"),
                Height = sz.AttrInt("height"),
                Depth = t.Int("depth"),
            });
        }
        return r;
    }

    /// <summary>gmksplit never keeps instance or tile ids by default: they are dealt out in room order.</summary>
    private static void NumberInstancesAndTiles(GameData g)
    {
        var instance = 100001;
        var tile = 10000001;
        foreach (var r in g.Rooms)
        {
            if (r == null) continue;
            foreach (var i in r.Instances) i.Id = instance++;
            foreach (var t in r.Tiles) t.Id = tile++;
        }
        g.LastInstanceId = instance - 1;
        g.LastTileId = tile - 1;
    }

    //---------------------------------------------------------------------------
    // Everything else
    //---------------------------------------------------------------------------

    private List<Constant> ReadConstants()
    {
        var path = TreePath.Combine(_root, "Constants.xml");
        return Xml.Load(path).Expect("constants").Children("constant")
            .Select(c => new Constant(GmText.Store(c.Attr("name")), GmText.Store(c.Attr("value")))).ToList();
    }

    private static readonly string[] ExportOptions = ["NO_AUTO_EXPORT", "TEMP_DIRECTORY", "WORKING_DIRECTORY", "OTHER_DIRECTORY"];

    private List<IncludedFile> ReadIncludedFiles()
    {
        var dir = TreePath.Combine(_root, "Included Files");
        var list = new List<IncludedFile>();
        if (!Directory.Exists(dir)) return list;
        foreach (var meta in TreePath.Files(dir, "*.meta.xml"))
        {
            var x = Xml.Load(meta).Expect("include");
            var f = new IncludedFile
            {
                FileName = GmText.Store(x.Str("filename")),
                SourcePath = GmText.Store(x.Str("filepath")),
                StoredInGame = x.Bool("original"),
                SourceLength = (uint)x.Int("originalSize"),
                Export = (uint)Lookup(ExportOptions, x.Str("exportTo"), x, "exportTo"),
                ExportFolder = GmText.Store(x.Str("otherExportDirectory")),
                Overwrite = x.Bool("overwriteExisting"),
                FreeMemory = x.Bool("freeMemAfterExport"),
                RemoveAtEnd = x.Bool("removeAtGameEnd"),
            };
            var data = meta[..^".meta.xml".Length];
            if (File.Exists(data))
            {
                f.Data = File.ReadAllBytes(data);
                f.DataExists = true;
            }
            list.Add(f);
        }
        return list;
    }

    private GameInformation ReadGameInformation()
    {
        var x = Xml.Load(TreePath.Combine(_root, "Game Information.xml")).Expect("gameInformation");
        var pos = x.Child("windowPosition");
        return new GameInformation
        {
            Colour = Colour(x.Str("backgroundColor")),
            SeparateWindow = !x.Bool("mimicGameWindow"),
            Caption = GmText.Store(x.Str("formCaption")),
            Left = pos.Int("left"),
            Top = pos.Int("top"),
            Width = pos.Int("width"),
            Height = pos.Int("height"),
            ShowBorder = x.Bool("showBorder"),
            Resizable = x.Bool("allowResize"),
            OnTop = x.Bool("stayOnTop"),
            FreezeGame = x.Bool("pauseGame"),
            Rtf = GmText.ReadFile(TreePath.Combine(_root, "Game Information.txt")),
        };
    }

    /// <summary>The version resource's contents. Unicode, unlike the game's own text: it goes into the runner as UTF-16.</summary>
    private VersionInfo ReadVersion()
    {
        var info = Xml.Load(TreePath.Combine(_root, "Global Game Settings.xml")).Expect("settings").Child("gameInfo");
        return new VersionInfo(info.Int("versionMajor"), info.Int("versionMinor"), info.Int("versionRelease"), info.Int("versionBuild"),
            info.Str("company"), info.Str("product"), info.Str("copyright"), info.Str("description"));
    }

    private List<string> ReadExtensionNames()
    {
        var path = TreePath.Combine(_root, "Extension Packages.xml");
        if (!File.Exists(path)) return [];
        return Xml.Load(path).Expect("extensionPackages").Children("package").Select(p => p.Text).ToList();
    }

    private Settings ReadSettings(GameData g)
    {
        var x = Xml.Load(TreePath.Combine(_root, "Global Game Settings.xml")).Expect("settings");
        var gr = x.Child("graphics");
        var win = x.Child("windowing");
        var splash = x.Child("splashImage");
        var bar = x.Child("progressBar");
        var keys = x.Child("keys");
        var err = x.Child("errors");
        var info = x.Child("gameInfo");
        var sys = x.Child("system");

        var s = new Settings
        {
            Scaling = gr.Int("scalingPercent"),
            ShowCursor = gr.Bool("displayCursor"),
            VSync = gr.Bool("useVsync"),
            Interpolate = gr.Bool("interpolateColors"),
            ColourOutsideRoom = Colour(gr.Str("colorOutsideRoom")),
            Fullscreen = win.Bool("startFullscreen"),
            NoBorder = win.Bool("dontDrawBorder"),
            AllowResize = win.Bool("allowWindowResize"),
            AlwaysOnTop = win.Bool("alwaysOnTop"),
            NoButtons = win.Bool("dontShowButtons"),
            SetResolution = win.Bool("switchVideoMode"),
            LoadingTransparent = splash.Bool("partiallyTransparent"),
            LoadingAlpha = (uint)splash.Int("alphaTransparency"),
            LoadingBar = (uint)Lookup(["LOADBAR_NONE", "LOADBAR_DEFAULT", "LOADBAR_CUSTOM"], bar.Str("mode"), bar, "progress bar"),
            ScaleProgressBar = bar.Bool("scaleImage"),
            LetF1ShowInfo = keys.Bool("letF1ShowGameInfo"),
            LetF4SwitchFullscreen = keys.Bool("letF4SwitchFullscreen"),
            LetF5SaveF6Load = keys.Bool("letF5SaveF6Load"),
            LetF9Screenshot = keys.Bool("letF9Screenshot"),
            LetEscEndGame = keys.Bool("letEscEndGame"),
            TreatCloseAsEsc = keys.Bool("treatCloseAsEscape"),
            DisplayErrors = err.Bool("displayErrors"),
            WriteErrors = err.Bool("writeToLog"),
            AbortOnError = err.Bool("abortOnError"),
            TreatUninitialisedAsZero = err.Bool("treatUninitializedAsZero"),
            Priority = (uint)Lookup(["PRIORITY_NORMAL", "PRIORITY_HIGH", "PRIORITY_HIGHEST"], sys.Str("processPriority"), sys, "priority"),
            DisableScreensaver = sys.Bool("disableScreensavers"),
            FreezeOnLoseFocus = sys.Bool("freezeOnLoseFocus"),
        };
        if (s.SetResolution)
        {
            var vm = win.Child("videoMode");
            s.ColourDepth = (uint)Lookup(["COLOR_NOCHANGE", "COLOR_16", "COLOR_32"], vm.Str("colorDepth"), vm, "colour depth");
            s.Resolution = (uint)Lookup(["RES_NOCHANGE", "RES_320X240", "RES_640X480", "RES_800X600", "RES_1024X768", "RES_1280X1024", "RES_1600X1200"], vm.Str("resolution"), vm, "resolution");
            s.Frequency = (uint)Lookup(["FREQ_NOCHANGE", "FREQ_60", "FREQ_70", "FREQ_85", "FREQ_100", "FREQ_120"], vm.Str("frequency"), vm, "frequency");
        }

        if (splash.Bool("showCustom"))
        {
            var image = TreePath.Combine(_root, "loading image.png");
            if (File.Exists(image)) s.LoadingImage = SettingsImages.Encode(Png.Read(image));
        }
        if (s.LoadingBar == 2)
        {
            var back = TreePath.Combine(_root, "loadbar background.png");
            var front = TreePath.Combine(_root, "loadbar front.png");
            if (File.Exists(back)) s.LoadingBarBack = SettingsImages.Encode(Png.Read(back));
            if (File.Exists(front)) s.LoadingBarFront = SettingsImages.Encode(Png.Read(front));
        }

        g.GameId = (uint)info.Int("gameId");
        if (info.Has("directPlayGuid")) g.Guid = Convert.FromHexString(info.Str("directPlayGuid").Trim());
        return s;
    }
}
