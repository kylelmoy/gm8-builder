using Gm8Builder.IO;

namespace Gm8Builder.Install;

/// <summary>
/// An action library (lib/*.lib): the drag-and-drop actions on one tab of the
/// object editor. A build needs its init code; the functions its actions call
/// are also callable from GML, though Game Maker's fnames does not list them.
/// </summary>
public sealed class ActionLibrary
{
    public string Caption = "";
    public uint Id;
    public string InitCode = "";
    /// <summary>The functions this library's function-type actions call, e.g. action_move.</summary>
    public List<string> Functions = [];

    public static ActionLibrary Read(string path)
    {
        var r = new ByteReader(File.ReadAllBytes(path));
        var version = r.U32();
        if (version != 500 && version != 520) throw new InvalidDataException($"{path}: library version {version}");
        var lib = new ActionLibrary { Caption = r.Str(), Id = r.U32() };
        r.Str(); // author
        r.U32(); // version
        r.F64(); // last changed
        r.Str(); // info
        lib.InitCode = r.Str();
        r.U32(); // advanced
        r.U32(); // next action id

        var count = r.U32();
        for (var i = 0; i < count; i++)
        {
            var actionVersion = r.U32();
            r.Str(); // name
            r.U32(); // id
            r.Chunk(); // image
            r.U32(); // hidden
            r.U32(); // advanced
            if (actionVersion == 520) r.U32(); // registered only
            r.Str(); // description
            r.Str(); // list text
            r.Str(); // hint text
            r.U32(); // kind
            r.U32(); // interface
            r.U32(); // question
            r.U32(); // apply to
            r.U32(); // relative
            r.U32(); // argument count
            var slots = r.U32();
            for (var a = 0; a < slots; a++)
            {
                r.Str(); // caption
                r.U32(); // kind
                r.Str(); // default
                r.Str(); // menu
            }
            var execution = r.U32();
            var function = r.Str();
            r.Str(); // code
            if (execution == 1 && function != "") lib.Functions.Add(function);
        }
        r.ExpectEnd(path);
        return lib;
    }

    /// <summary>Every library in an install's lib directory, in file-name order - the order Game Maker loads them.</summary>
    public static List<ActionLibrary> ReadAll(string gm8Dir)
    {
        var dir = Path.Combine(gm8Dir, "lib");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.lib").Order(StringComparer.OrdinalIgnoreCase).Select(Read).ToList();
    }
}
