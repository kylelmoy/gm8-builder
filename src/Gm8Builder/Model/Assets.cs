using Gm8Builder.IO;

namespace Gm8Builder.Model;

//=============================================================================
// The assets of a GM8.0 executable, in the form the runner reads them.
//
// Each Read/Write pair covers an asset's body: the bytes after the leading
// "exists" u32 of its zlib blob (GameData handles that and deleted slots). The
// pairs are exact inverses; GameData's round-trip check proves that on a real
// build, so a field here that GM8 writes as something other than we assume
// fails loudly rather than being normalised away.
//
// Layouts are confirmed against executables built by Game Maker 8.0.
//=============================================================================

public interface IAsset<T> where T : IAsset<T>
{
    static abstract T Read(ByteReader r);
    void Write(ByteWriter w);
}

public sealed class Trigger : IAsset<Trigger>
{
    public string Name = "";
    public string Condition = "";
    /// <summary>0 step, 1 begin step, 2 end step.</summary>
    public uint Moment;
    public string ConstantName = "";

    public static Trigger Read(ByteReader r)
    {
        r.Expect(800, "trigger");
        return new Trigger { Name = r.Str(), Condition = r.Str(), Moment = r.U32(), ConstantName = r.Str() };
    }

    public void Write(ByteWriter w)
    {
        w.U32(800);
        w.Str(Name);
        w.Str(Condition);
        w.U32(Moment);
        w.Str(ConstantName);
    }
}

public sealed record Constant(string Name, string Value);

public sealed class Sound : IAsset<Sound>
{
    public string Name = "";
    /// <summary>0 normal, 1 background music, 2 3D, 3 multimedia player.</summary>
    public uint Kind;
    public string FileType = "";
    public string FileName = "";
    public byte[]? Data;
    /// <summary>Bit flags: chorus, echo, flanger, gargle, reverb.</summary>
    public uint Effects;
    public double Volume;
    public double Pan;
    public bool Preload;

    public static Sound Read(ByteReader r)
    {
        var s = new Sound { Name = r.Str() };
        r.Expect(800, $"sound {s.Name}");
        s.Kind = r.U32();
        s.FileType = r.Str();
        s.FileName = r.Str();
        s.Data = r.Bool() ? r.Chunk() : null;
        s.Effects = r.U32();
        s.Volume = r.F64();
        s.Pan = r.F64();
        s.Preload = r.Bool();
        return s;
    }

    public void Write(ByteWriter w)
    {
        w.Str(Name);
        w.U32(800);
        w.U32(Kind);
        w.Str(FileType);
        w.Str(FileName);
        w.Bool(Data != null);
        if (Data != null) w.Chunk(Data);
        w.U32(Effects);
        w.F64(Volume);
        w.F64(Pan);
        w.Bool(Preload);
    }
}

/// <summary>Raw 32-bit pixels, as the runner uploads them.</summary>
public sealed class Frame
{
    public uint Width;
    public uint Height;
    public byte[] Pixels = [];
}

/// <summary>A collision mask: one flag per pixel of the frame, and the box around the set ones.</summary>
public sealed class CollisionMask
{
    public uint Width;
    public uint Height;
    public uint Left;
    public uint Right;
    public uint Bottom;
    public uint Top;
    /// <summary>0 or 1 per pixel, row-major.</summary>
    public byte[] Bits = [];
}

public sealed class Sprite : IAsset<Sprite>
{
    public string Name = "";
    public int OriginX;
    public int OriginY;
    public List<Frame> Frames = [];
    /// <summary>One mask per frame when true, otherwise one for all of them.</summary>
    public bool SeparateMasks;
    public List<CollisionMask> Masks = [];

    public static Sprite Read(ByteReader r)
    {
        var s = new Sprite { Name = r.Str() };
        r.Expect(800, $"sprite {s.Name}");
        s.OriginX = r.I32();
        s.OriginY = r.I32();
        var frames = r.U32();
        for (var i = 0; i < frames; i++)
        {
            r.Expect(800, $"sprite {s.Name} frame");
            var f = new Frame { Width = r.U32(), Height = r.U32() };
            f.Pixels = r.Chunk();
            if (f.Pixels.Length != f.Width * f.Height * 4)
                throw new InvalidDataException($"sprite {s.Name}: frame of {f.Width}x{f.Height} holds {f.Pixels.Length} bytes");
            s.Frames.Add(f);
        }

        // Written even with no frames, which then have no mask either.
        s.SeparateMasks = r.Bool();
        var masks = frames == 0 ? 0 : s.SeparateMasks ? frames : 1;
        for (var i = 0; i < masks; i++)
        {
            r.Expect(800, $"sprite {s.Name} mask");
            var m = new CollisionMask
            {
                Width = r.U32(),
                Height = r.U32(),
                Left = r.U32(),
                Right = r.U32(),
                Bottom = r.U32(),
                Top = r.U32(),
            };
            m.Bits = new byte[checked((int)(m.Width * m.Height))];
            for (var p = 0; p < m.Bits.Length; p++) m.Bits[p] = r.Bool() ? (byte)1 : (byte)0;
            s.Masks.Add(m);
        }
        return s;
    }

    public void Write(ByteWriter w)
    {
        w.Str(Name);
        w.U32(800);
        w.I32(OriginX);
        w.I32(OriginY);
        w.U32((uint)Frames.Count);
        foreach (var f in Frames)
        {
            w.U32(800);
            w.U32(f.Width);
            w.U32(f.Height);
            w.Chunk(f.Pixels);
        }
        w.Bool(SeparateMasks);
        foreach (var m in Masks)
        {
            w.U32(800);
            w.U32(m.Width);
            w.U32(m.Height);
            w.U32(m.Left);
            w.U32(m.Right);
            w.U32(m.Bottom);
            w.U32(m.Top);
            foreach (var b in m.Bits) w.U32(b);
        }
    }
}

public sealed class Background : IAsset<Background>
{
    public string Name = "";
    public uint Width;
    public uint Height;
    /// <summary>Absent when either dimension is zero.</summary>
    public byte[]? Pixels;

    public static Background Read(ByteReader r)
    {
        var b = new Background { Name = r.Str() };
        r.Expect(710, $"background {b.Name}");
        r.Expect(800, $"background {b.Name}");
        b.Width = r.U32();
        b.Height = r.U32();
        if (b.Width > 0 && b.Height > 0)
        {
            b.Pixels = r.Chunk();
            if (b.Pixels.Length != b.Width * b.Height * 4)
                throw new InvalidDataException($"background {b.Name}: {b.Width}x{b.Height} holds {b.Pixels.Length} bytes");
        }
        return b;
    }

    public void Write(ByteWriter w)
    {
        w.Str(Name);
        w.U32(710);
        w.U32(800);
        w.U32(Width);
        w.U32(Height);
        if (Pixels != null) w.Chunk(Pixels);
    }
}

public sealed record PathPoint(double X, double Y, double Speed);

public sealed class GamePath : IAsset<GamePath>
{
    public string Name = "";
    /// <summary>0 straight lines, 1 smooth curve.</summary>
    public uint Kind;
    public bool Closed;
    public uint Precision;
    public List<PathPoint> Points = [];

    public static GamePath Read(ByteReader r)
    {
        var p = new GamePath { Name = r.Str() };
        r.Expect(530, $"path {p.Name}");
        p.Kind = r.U32();
        p.Closed = r.Bool();
        p.Precision = r.U32();
        var n = r.U32();
        for (var i = 0; i < n; i++) p.Points.Add(new PathPoint(r.F64(), r.F64(), r.F64()));
        return p;
    }

    public void Write(ByteWriter w)
    {
        w.Str(Name);
        w.U32(530);
        w.U32(Kind);
        w.Bool(Closed);
        w.U32(Precision);
        w.U32((uint)Points.Count);
        foreach (var pt in Points)
        {
            w.F64(pt.X);
            w.F64(pt.Y);
            w.F64(pt.Speed);
        }
    }
}

public sealed class Script : IAsset<Script>
{
    public string Name = "";
    public string Code = "";

    public static Script Read(ByteReader r)
    {
        var s = new Script { Name = r.Str() };
        r.Expect(800, $"script {s.Name}");
        s.Code = r.Str();
        return s;
    }

    public void Write(ByteWriter w)
    {
        w.Str(Name);
        w.U32(800);
        w.Str(Code);
    }
}

public sealed class Font : IAsset<Font>
{
    public const int GlyphFields = 0x600;

    public string Name = "";
    public string FontName = "";
    public uint Size;
    public bool Bold;
    public bool Italic;
    public uint RangeStart;
    public uint RangeEnd;
    /// <summary>Six u32 per character 0-255: x, y, width, height, offset, advance.</summary>
    public uint[] Glyphs = new uint[GlyphFields];
    public uint MapWidth;
    public uint MapHeight;
    /// <summary>One alpha byte per pixel of the MapWidth x MapHeight atlas.</summary>
    public byte[] Map = [];

    public static Font Read(ByteReader r)
    {
        var f = new Font { Name = r.Str() };
        r.Expect(800, $"font {f.Name}");
        f.FontName = r.Str();
        f.Size = r.U32();
        f.Bold = r.Bool();
        f.Italic = r.Bool();
        f.RangeStart = r.U32();
        f.RangeEnd = r.U32();
        for (var i = 0; i < GlyphFields; i++) f.Glyphs[i] = r.U32();
        f.MapWidth = r.U32();
        f.MapHeight = r.U32();
        f.Map = r.Chunk();
        return f;
    }

    public void Write(ByteWriter w)
    {
        w.Str(Name);
        w.U32(800);
        w.Str(FontName);
        w.U32(Size);
        w.Bool(Bold);
        w.Bool(Italic);
        w.U32(RangeStart);
        w.U32(RangeEnd);
        foreach (var g in Glyphs) w.U32(g);
        w.U32(MapWidth);
        w.U32(MapHeight);
        w.Chunk(Map);
    }
}

/// <summary>
/// A drag-and-drop action, "execute code" included. Most of it - kind, function,
/// argument kinds - is copied from the action's definition in its library, not
/// from the project.
/// </summary>
public sealed class Action
{
    public const int MaxArgs = 8;

    public uint LibraryId;
    public uint ActionId;
    public uint Kind;
    public bool MayBeRelative;
    public bool IsQuestion;
    public bool AppliesToSomething;
    /// <summary>0 nothing, 1 function, 2 code.</summary>
    public uint ExecutionType;
    public string FunctionName = "";
    public string Code = "";
    public uint ArgCount;
    public uint[] ArgKinds = new uint[MaxArgs];
    /// <summary>-1 self, -2 other, otherwise an object index.</summary>
    public int AppliesTo;
    public bool Relative;
    /// <summary>Game Maker fills the slots past <see cref="ArgCount"/> with "0".</summary>
    public string[] Args = Enumerable.Repeat("0", MaxArgs).ToArray();
    public bool Not;

    public static Action Read(ByteReader r)
    {
        r.Expect(440, "action");
        var a = new Action
        {
            LibraryId = r.U32(),
            ActionId = r.U32(),
            Kind = r.U32(),
            MayBeRelative = r.Bool(),
            IsQuestion = r.Bool(),
            AppliesToSomething = r.Bool(),
            ExecutionType = r.U32(),
            FunctionName = r.Str(),
            Code = r.Str(),
            ArgCount = r.U32(),
        };
        r.Expect(MaxArgs, "action argument kinds");
        for (var i = 0; i < MaxArgs; i++) a.ArgKinds[i] = r.U32();
        a.AppliesTo = r.I32();
        a.Relative = r.Bool();
        r.Expect(MaxArgs, "action arguments");
        for (var i = 0; i < MaxArgs; i++) a.Args[i] = r.Str();
        a.Not = r.Bool();
        return a;
    }

    public void Write(ByteWriter w)
    {
        w.U32(440);
        w.U32(LibraryId);
        w.U32(ActionId);
        w.U32(Kind);
        w.Bool(MayBeRelative);
        w.Bool(IsQuestion);
        w.Bool(AppliesToSomething);
        w.U32(ExecutionType);
        w.Str(FunctionName);
        w.Str(Code);
        w.U32(ArgCount);
        w.U32(MaxArgs);
        foreach (var k in ArgKinds) w.U32(k);
        w.I32(AppliesTo);
        w.Bool(Relative);
        w.U32(MaxArgs);
        foreach (var s in Args) w.Str(s);
        w.Bool(Not);
    }

    internal static List<Action> ReadList(ByteReader r, string what)
    {
        r.Expect(400, what);
        var n = r.U32();
        var list = new List<Action>((int)Math.Min(n, 1024));
        for (var i = 0; i < n; i++) list.Add(Read(r));
        return list;
    }

    internal static void WriteList(ByteWriter w, List<Action> actions)
    {
        w.U32(400);
        w.U32((uint)actions.Count);
        foreach (var a in actions) a.Write(w);
    }
}

public sealed class Timeline : IAsset<Timeline>
{
    public string Name = "";
    public List<(uint Step, List<Action> Actions)> Moments = [];

    public static Timeline Read(ByteReader r)
    {
        var t = new Timeline { Name = r.Str() };
        r.Expect(500, $"timeline {t.Name}");
        var n = r.U32();
        for (var i = 0; i < n; i++)
        {
            var step = r.U32();
            t.Moments.Add((step, Action.ReadList(r, $"timeline {t.Name} moment {step}")));
        }
        return t;
    }

    public void Write(ByteWriter w)
    {
        w.Str(Name);
        w.U32(500);
        w.U32((uint)Moments.Count);
        foreach (var (step, actions) in Moments)
        {
            w.U32(step);
            Action.WriteList(w, actions);
        }
    }
}

public sealed class GameObject : IAsset<GameObject>
{
    /// <summary>Create, destroy, alarm, step, collision, keyboard, mouse, other, draw, key press, key release, trigger.</summary>
    public const int EventTypes = 12;

    public string Name = "";
    public int Sprite = -1;
    public bool Solid;
    public bool Visible;
    public int Depth;
    public bool Persistent;
    /// <summary>-100 for none, unlike every other reference, which uses -1.</summary>
    public int Parent = -100;
    public int Mask = -1;
    /// <summary>Per event type, (sub-event number, actions), in the order stored.</summary>
    public List<(uint Sub, List<Action> Actions)>[] Events =
        Enumerable.Range(0, EventTypes).Select(_ => new List<(uint, List<Action>)>()).ToArray();

    public static GameObject Read(ByteReader r)
    {
        var o = new GameObject { Name = r.Str() };
        r.Expect(430, $"object {o.Name}");
        o.Sprite = r.I32();
        o.Solid = r.Bool();
        o.Visible = r.Bool();
        o.Depth = r.I32();
        o.Persistent = r.Bool();
        o.Parent = r.I32();
        o.Mask = r.I32();
        r.Expect(EventTypes - 1, $"object {o.Name} event count");
        for (var type = 0; type < EventTypes; type++)
        {
            while (true)
            {
                var sub = r.I32();
                if (sub == -1) break;
                if (sub < 0) throw new InvalidDataException($"object {o.Name}: sub-event {sub}");
                o.Events[type].Add(((uint)sub, Action.ReadList(r, $"object {o.Name} event {type}/{sub}")));
            }
        }
        return o;
    }

    public void Write(ByteWriter w)
    {
        w.Str(Name);
        w.U32(430);
        w.I32(Sprite);
        w.Bool(Solid);
        w.Bool(Visible);
        w.I32(Depth);
        w.Bool(Persistent);
        w.I32(Parent);
        w.I32(Mask);
        w.U32(EventTypes - 1);
        foreach (var list in Events)
        {
            foreach (var (sub, actions) in list)
            {
                w.U32(sub);
                Action.WriteList(w, actions);
            }
            w.I32(-1);
        }
    }
}

public sealed class RoomBackground
{
    public bool Visible;
    public bool Foreground;
    public int Background = -1;
    public int X;
    public int Y;
    public bool TileH;
    public bool TileV;
    public int HSpeed;
    public int VSpeed;
    public bool Stretch;
}

public sealed class View
{
    public bool Visible;
    public int ViewX;
    public int ViewY;
    public uint ViewW;
    public uint ViewH;
    public int PortX;
    public int PortY;
    public uint PortW;
    public uint PortH;
    public int HBorder;
    public int VBorder;
    public int HSpeed;
    public int VSpeed;
    public int Follow = -1;
}

public sealed class Instance
{
    public int X;
    public int Y;
    public int Object;
    public int Id;
    public string CreationCode = "";
}

public sealed class Tile
{
    public int X;
    public int Y;
    public int Background;
    public int TileX;
    public int TileY;
    public int Width;
    public int Height;
    public int Depth;
    public int Id;
}

public sealed class Room : IAsset<Room>
{
    public string Name = "";
    public string Caption = "";
    public uint Width;
    public uint Height;
    public uint Speed;
    public bool Persistent;
    public uint Colour;
    public bool ClearScreen;
    public string CreationCode = "";
    public List<RoomBackground> Backgrounds = [];
    public bool ViewsEnabled;
    public List<View> Views = [];
    public List<Instance> Instances = [];
    public List<Tile> Tiles = [];

    public static Room Read(ByteReader r)
    {
        var m = new Room { Name = r.Str() };
        r.Expect(541, $"room {m.Name}");
        m.Caption = r.Str();
        m.Width = r.U32();
        m.Height = r.U32();
        m.Speed = r.U32();
        m.Persistent = r.Bool();
        m.Colour = r.U32();
        m.ClearScreen = r.Bool();
        m.CreationCode = r.Str();

        var n = r.U32();
        for (var i = 0; i < n; i++)
        {
            m.Backgrounds.Add(new RoomBackground
            {
                Visible = r.Bool(),
                Foreground = r.Bool(),
                Background = r.I32(),
                X = r.I32(),
                Y = r.I32(),
                TileH = r.Bool(),
                TileV = r.Bool(),
                HSpeed = r.I32(),
                VSpeed = r.I32(),
                Stretch = r.Bool(),
            });
        }

        m.ViewsEnabled = r.Bool();
        n = r.U32();
        for (var i = 0; i < n; i++)
        {
            m.Views.Add(new View
            {
                Visible = r.Bool(),
                ViewX = r.I32(),
                ViewY = r.I32(),
                ViewW = r.U32(),
                ViewH = r.U32(),
                PortX = r.I32(),
                PortY = r.I32(),
                PortW = r.U32(),
                PortH = r.U32(),
                HBorder = r.I32(),
                VBorder = r.I32(),
                HSpeed = r.I32(),
                VSpeed = r.I32(),
                Follow = r.I32(),
            });
        }

        n = r.U32();
        for (var i = 0; i < n; i++)
            m.Instances.Add(new Instance { X = r.I32(), Y = r.I32(), Object = r.I32(), Id = r.I32(), CreationCode = r.Str() });

        n = r.U32();
        for (var i = 0; i < n; i++)
        {
            m.Tiles.Add(new Tile
            {
                X = r.I32(),
                Y = r.I32(),
                Background = r.I32(),
                TileX = r.I32(),
                TileY = r.I32(),
                Width = r.I32(),
                Height = r.I32(),
                Depth = r.I32(),
                Id = r.I32(),
            });
        }
        return m;
    }

    public void Write(ByteWriter w)
    {
        w.Str(Name);
        w.U32(541);
        w.Str(Caption);
        w.U32(Width);
        w.U32(Height);
        w.U32(Speed);
        w.Bool(Persistent);
        w.U32(Colour);
        w.Bool(ClearScreen);
        w.Str(CreationCode);

        w.U32((uint)Backgrounds.Count);
        foreach (var b in Backgrounds)
        {
            w.Bool(b.Visible);
            w.Bool(b.Foreground);
            w.I32(b.Background);
            w.I32(b.X);
            w.I32(b.Y);
            w.Bool(b.TileH);
            w.Bool(b.TileV);
            w.I32(b.HSpeed);
            w.I32(b.VSpeed);
            w.Bool(b.Stretch);
        }

        w.Bool(ViewsEnabled);
        w.U32((uint)Views.Count);
        foreach (var v in Views)
        {
            w.Bool(v.Visible);
            w.I32(v.ViewX);
            w.I32(v.ViewY);
            w.U32(v.ViewW);
            w.U32(v.ViewH);
            w.I32(v.PortX);
            w.I32(v.PortY);
            w.U32(v.PortW);
            w.U32(v.PortH);
            w.I32(v.HBorder);
            w.I32(v.VBorder);
            w.I32(v.HSpeed);
            w.I32(v.VSpeed);
            w.I32(v.Follow);
        }

        w.U32((uint)Instances.Count);
        foreach (var i in Instances)
        {
            w.I32(i.X);
            w.I32(i.Y);
            w.I32(i.Object);
            w.I32(i.Id);
            w.Str(i.CreationCode);
        }

        w.U32((uint)Tiles.Count);
        foreach (var t in Tiles)
        {
            w.I32(t.X);
            w.I32(t.Y);
            w.I32(t.Background);
            w.I32(t.TileX);
            w.I32(t.TileY);
            w.I32(t.Width);
            w.I32(t.Height);
            w.I32(t.Depth);
            w.I32(t.Id);
        }
    }
}

public sealed class IncludedFile
{
    public string FileName = "";
    public string SourcePath = "";
    public bool DataExists;
    public uint SourceLength;
    public bool StoredInGame;
    public byte[]? Data;
    /// <summary>0 don't export, 1 temp folder, 2 game folder, 3 <see cref="ExportFolder"/>.</summary>
    public uint Export;
    public string ExportFolder = "";
    public bool Overwrite;
    public bool FreeMemory;
    public bool RemoveAtEnd;

    /// <summary>Unlike the other assets there is no leading "exists" flag.</summary>
    public static IncludedFile Read(ByteReader r)
    {
        r.Expect(800, "included file");
        var f = new IncludedFile
        {
            FileName = r.Str(),
            SourcePath = r.Str(),
            DataExists = r.Bool(),
            SourceLength = r.U32(),
            StoredInGame = r.Bool(),
        };
        if (f.DataExists && f.StoredInGame) f.Data = r.Chunk();
        f.Export = r.U32();
        f.ExportFolder = r.Str();
        f.Overwrite = r.Bool();
        f.FreeMemory = r.Bool();
        f.RemoveAtEnd = r.Bool();
        return f;
    }

    public void Write(ByteWriter w)
    {
        w.U32(800);
        w.Str(FileName);
        w.Str(SourcePath);
        w.Bool(DataExists);
        w.U32(SourceLength);
        w.Bool(StoredInGame);
        if (DataExists && StoredInGame) w.Chunk(Data ?? []);
        w.U32(Export);
        w.Str(ExportFolder);
        w.Bool(Overwrite);
        w.Bool(FreeMemory);
        w.Bool(RemoveAtEnd);
    }
}

/// <summary>The Game Information window: its settings and the RTF shown in it.</summary>
public sealed class GameInformation
{
    public uint Colour;
    public bool SeparateWindow;
    public string Caption = "";
    public int Left;
    public int Top;
    public int Width;
    public int Height;
    public bool ShowBorder;
    public bool Resizable;
    public bool OnTop;
    public bool FreezeGame;
    public string Rtf = "";

    public static GameInformation Read(ByteReader r) => new()
    {
        Colour = r.U32(),
        SeparateWindow = r.Bool(),
        Caption = r.Str(),
        Left = r.I32(),
        Top = r.I32(),
        Width = r.I32(),
        Height = r.I32(),
        ShowBorder = r.Bool(),
        Resizable = r.Bool(),
        OnTop = r.Bool(),
        FreezeGame = r.Bool(),
        Rtf = r.Str(),
    };

    public void Write(ByteWriter w)
    {
        w.U32(Colour);
        w.Bool(SeparateWindow);
        w.Str(Caption);
        w.I32(Left);
        w.I32(Top);
        w.I32(Width);
        w.I32(Height);
        w.Bool(ShowBorder);
        w.Bool(Resizable);
        w.Bool(OnTop);
        w.Bool(FreezeGame);
        w.Str(Rtf);
    }
}
