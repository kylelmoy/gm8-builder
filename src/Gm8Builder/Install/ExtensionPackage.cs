using System.Buffers.Binary;
using Gm8Builder.Exe;
using Gm8Builder.IO;
using Gm8Builder.Model;

namespace Gm8Builder.Install;

/// <summary>
/// An extension package as Game Maker installs it: a description (.ged) and
/// its files' contents (.dat). A built game embeds the description, minus the
/// editor-only parts, and the .dat verbatim.
///
/// The same description opens a .gex (the installer a package is distributed
/// as), encrypted, and followed by the files compressed differently.
/// </summary>
public sealed class ExtensionPackage
{
    public string Name = "";
    public string FolderName = "";
    public string Version = "";
    public List<PackageFile> Files = [];

    public sealed class PackageFile
    {
        public string FileName = "";
        public uint Kind;
        public string Initializer = "";
        public string Finalizer = "";
        public List<(string Name, string ExternalName, uint Convention, int ArgCount, uint[] ArgTypes, uint ReturnType)> Functions = [];
        public List<Constant> Constants = [];
    }

    /// <summary>Seed and encrypted contents, as the game stores them; null for a .gex, whose files are re-packed.</summary>
    public (uint Seed, byte[] Payload)? Data;

    /// <summary>A .gex's file contents, inflated, in file order (null for action libraries).</summary>
    public List<byte[]?>? Contents;

    public static ExtensionPackage ReadInstalled(string ged, string dat)
    {
        var p = ReadDescription(new ByteReader(File.ReadAllBytes(ged)), ged);
        var d = File.ReadAllBytes(dat);
        p.Data = (BinaryPrimitives.ReadUInt32LittleEndian(d), d.AsSpan(4).ToArray());
        return p;
    }

    /// <summary>
    /// A .gex: u32 1234321, u32 701, u32 seed, then everything else under
    /// <see cref="ExtensionCipher"/>: the description, then per file [len][zlib].
    /// </summary>
    public static ExtensionPackage ReadGex(string path)
    {
        var raw = File.ReadAllBytes(path);
        if (raw.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(raw) != 1234321)
            throw new InvalidDataException($"{path}: not a Game Maker extension package");
        var seed = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8));
        var r = new ByteReader(ExtensionCipher.Decrypt(raw.AsSpan(12), seed));
        var p = ReadDescription(r, path);
        p.Contents = [];
        foreach (var f in p.Files) p.Contents.Add(f.Kind == 3 ? null : Zlib.Inflate(r.Chunk()));
        return p;
    }

    private static ExtensionPackage ReadDescription(ByteReader r, string path)
    {
        r.Expect(700, $"{path}: package");
        r.U32(); // editable
        var p = new ExtensionPackage { Name = r.Str(), FolderName = r.Str(), Version = r.Str() };
        r.Str(); // author
        r.Str(); // date
        r.Str(); // licence
        r.Str(); // description
        r.Str(); // help file
        r.U32(); // hidden
        var uses = r.U32();
        for (var i = 0; i < uses; i++) r.Str();

        var files = r.U32();
        for (var i = 0; i < files; i++)
        {
            r.Expect(700, $"{path}: file");
            var f = new PackageFile { FileName = r.Str() };
            r.Str(); // original path
            f.Kind = r.U32();
            f.Initializer = r.Str();
            f.Finalizer = r.Str();

            var n = r.U32();
            for (var j = 0; j < n; j++)
            {
                r.Expect(700, $"{path}: function");
                var name = r.Str();
                var external = r.Str();
                var convention = r.U32();
                r.Str(); // help line
                r.U32(); // hidden
                var argc = r.I32();
                var types = new uint[ExtensionFunction.MaxArgs];
                for (var k = 0; k < types.Length; k++) types[k] = r.U32();
                f.Functions.Add((name, external, convention, argc, types, r.U32()));
            }

            n = r.U32();
            for (var j = 0; j < n; j++)
            {
                r.Expect(700, $"{path}: constant");
                f.Constants.Add(new Constant(r.Str(), r.Str()));
                r.U32(); // hidden
            }
            p.Files.Add(f);
        }
        return p;
    }

    /// <summary>
    /// The package as a game embeds it. Function ids are numbered across every
    /// extension in the game, so the caller passes the next free one in.
    /// </summary>
    public Extension ToExtension(ref uint nextFunctionId, Random rng)
    {
        var e = new Extension { Name = Name, FolderName = FolderName };
        foreach (var f in Files)
        {
            var ef = new ExtensionFile { Name = f.FileName, Kind = f.Kind, Initializer = f.Initializer, Finalizer = f.Finalizer, Constants = f.Constants };
            foreach (var fn in f.Functions)
            {
                ef.Functions.Add(new ExtensionFunction
                {
                    Name = fn.Name,
                    ExternalName = fn.ExternalName,
                    Convention = fn.Convention,
                    Id = nextFunctionId++,
                    ArgCount = fn.ArgCount,
                    ArgTypes = fn.ArgTypes,
                    ReturnType = fn.ReturnType,
                });
            }
            e.Files.Add(ef);
        }

        if (Data is var (seed, payload))
        {
            e.Seed = seed;
            e.Payload = ExtensionCipher.Decrypt(payload, seed);
        }
        else
        {
            var w = new ByteWriter();
            foreach (var c in Contents!)
            {
                if (c != null) w.Chunk(Zlib.Deflate(c));
            }
            e.Seed = (uint)rng.Next(0, 25000);
            e.Payload = w.ToArray();
        }
        return e;
    }
}
