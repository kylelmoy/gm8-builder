using Gm8Builder.Exe;
using Gm8Builder.IO;

namespace Gm8Builder.Model;

public sealed class ExtensionFunction
{
    public const int MaxArgs = 17;

    public string Name = "";
    public string ExternalName = "";
    /// <summary>2 GML, 11 stdcall, 12 cdecl.</summary>
    public uint Convention;
    public uint Id;
    public int ArgCount;
    /// <summary>1 string, 2 real.</summary>
    public uint[] ArgTypes = new uint[MaxArgs];
    public uint ReturnType;
}

public sealed class ExtensionFile
{
    public string Name = "";
    /// <summary>1 DLL, 2 GML, 3 action library, 4 other.</summary>
    public uint Kind;
    public string Initializer = "";
    public string Finalizer = "";
    public List<ExtensionFunction> Functions = [];
    public List<Constant> Constants = [];
}

/// <summary>
/// An extension package, as embedded in the game. Unlike the other assets these
/// are not zlib blobs: they sit in the stream directly, and only the files'
/// contents are compressed - each non-action-library file as [len][zlib], in
/// order - then encrypted together with <see cref="ExtensionCipher"/>.
/// </summary>
public sealed class Extension
{
    public string Name = "";
    public string FolderName = "";
    public List<ExtensionFile> Files = [];
    public uint Seed;
    /// <summary>The files' contents, decrypted but not inflated.</summary>
    public byte[] Payload = [];

    public static Extension Read(ByteReader r)
    {
        r.Expect(700, "extension");
        var e = new Extension { Name = r.Str(), FolderName = r.Str() };
        var files = r.U32();
        for (var i = 0; i < files; i++)
        {
            r.Expect(700, $"extension {e.Name} file");
            var f = new ExtensionFile { Name = r.Str(), Kind = r.U32(), Initializer = r.Str(), Finalizer = r.Str() };

            var n = r.U32();
            for (var j = 0; j < n; j++)
            {
                r.Expect(700, $"extension {e.Name} function");
                var fn = new ExtensionFunction
                {
                    Name = r.Str(),
                    ExternalName = r.Str(),
                    Convention = r.U32(),
                    Id = r.U32(),
                    ArgCount = r.I32(),
                };
                for (var k = 0; k < ExtensionFunction.MaxArgs; k++) fn.ArgTypes[k] = r.U32();
                fn.ReturnType = r.U32();
                f.Functions.Add(fn);
            }

            n = r.U32();
            for (var j = 0; j < n; j++)
            {
                r.Expect(700, $"extension {e.Name} constant");
                f.Constants.Add(new Constant(r.Str(), r.Str()));
            }
            e.Files.Add(f);
        }

        // The length counts the seed that follows it.
        var len = checked((int)r.U32()) - 4;
        e.Seed = r.U32();
        e.Payload = ExtensionCipher.Decrypt(r.Bytes(len), e.Seed);
        return e;
    }

    public void Write(ByteWriter w)
    {
        w.U32(700);
        w.Str(Name);
        w.Str(FolderName);
        w.U32((uint)Files.Count);
        foreach (var f in Files)
        {
            w.U32(700);
            w.Str(f.Name);
            w.U32(f.Kind);
            w.Str(f.Initializer);
            w.Str(f.Finalizer);
            w.U32((uint)f.Functions.Count);
            foreach (var fn in f.Functions)
            {
                w.U32(700);
                w.Str(fn.Name);
                w.Str(fn.ExternalName);
                w.U32(fn.Convention);
                w.U32(fn.Id);
                w.I32(fn.ArgCount);
                foreach (var t in fn.ArgTypes) w.U32(t);
                w.U32(fn.ReturnType);
            }
            w.U32((uint)f.Constants.Count);
            foreach (var c in f.Constants)
            {
                w.U32(700);
                w.Str(c.Name);
                w.Str(c.Value);
            }
        }
        w.U32((uint)Payload.Length + 4);
        w.U32(Seed);
        w.Bytes(ExtensionCipher.Encrypt(Payload, Seed));
    }

    /// <summary>Each file's inflated contents, or null for an action library (which stores none).</summary>
    public List<byte[]?> FileContents()
    {
        var r = new ByteReader(Payload);
        var list = new List<byte[]?>();
        foreach (var f in Files) list.Add(f.Kind == 3 ? null : Zlib.Inflate(r.Chunk()));
        return list;
    }
}
