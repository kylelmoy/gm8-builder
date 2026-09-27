using System.Security.Cryptography;
using Gm8Builder.IO;

namespace Gm8Builder.Model;

/// <summary>
/// Compressed forms of blobs already seen, by the hash of their inflated bytes.
/// Filled when an executable is read, so writing an unchanged asset back reuses
/// Game Maker's own compressed bytes: an unedited read/write is byte-identical,
/// and a rebuild only compresses what changed.
/// </summary>
public sealed class BlobCache
{
    private readonly Dictionary<string, byte[]> _compressed = [];
    private readonly Lock _lock = new();

    private static string Key(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw));

    public void Add(byte[] raw, byte[] compressed)
    {
        var key = Key(raw);
        lock (_lock) _compressed.TryAdd(key, compressed);
    }

    public byte[] Deflate(byte[] raw)
    {
        var key = Key(raw);
        lock (_lock)
        {
            if (_compressed.TryGetValue(key, out var hit)) return hit;
        }
        var compressed = Zlib.Deflate(raw);
        lock (_lock) _compressed.TryAdd(key, compressed);
        return compressed;
    }
}

/// <summary>
/// The decrypted asset stream: everything the runner loads after the settings
/// and the DirectX DLL. Deleted asset slots are null - they still occupy an
/// index, which is what GML and rooms refer to assets by.
/// </summary>
public sealed class GameData
{
    /// <summary>Random filler dwords that open the stream.</summary>
    public uint[] Garbage = [];
    public bool Pro;
    public uint GameId;
    public byte[] Guid = new byte[16];
    public List<Extension> Extensions = [];
    public List<Trigger?> Triggers = [];
    public List<Constant> Constants = [];
    public List<Sound?> Sounds = [];
    public List<Sprite?> Sprites = [];
    public List<Background?> Backgrounds = [];
    public List<GamePath?> Paths = [];
    public List<Script?> Scripts = [];
    public List<Font?> Fonts = [];
    public List<Timeline?> Timelines = [];
    public List<GameObject?> Objects = [];
    public List<Room?> Rooms = [];
    public int LastInstanceId;
    public int LastTileId;
    public List<IncludedFile> IncludedFiles = [];
    public GameInformation Information = new();
    /// <summary>Init code of each action library in use, run at game start in order.</summary>
    public List<string> LibraryInit = [];
    public List<int> RoomOrder = [];
    /// <summary>
    /// Bytes after the room order, which the runner never reads. Random-looking
    /// (several thousand of them in a typical build), so presumably more filler;
    /// carried through.
    /// </summary>
    public byte[] Trailer = [];

    public static GameData Read(byte[] stream, BlobCache? cache = null)
    {
        var r = new ByteReader(stream);
        var g = new GameData();

        g.Garbage = new uint[r.U32()];
        for (var i = 0; i < g.Garbage.Length; i++) g.Garbage[i] = r.U32();
        g.Pro = r.Bool();
        g.GameId = r.U32();
        g.Guid = r.Bytes(16);

        r.Expect(700, "extensions");
        var n = r.U32();
        for (var i = 0; i < n; i++) g.Extensions.Add(Extension.Read(r));

        g.Triggers = ReadSection<Trigger>(r, "triggers", cache);

        r.Expect(800, "constants");
        n = r.U32();
        for (var i = 0; i < n; i++) g.Constants.Add(new Constant(r.Str(), r.Str()));

        g.Sounds = ReadSection<Sound>(r, "sounds", cache);
        g.Sprites = ReadSection<Sprite>(r, "sprites", cache);
        g.Backgrounds = ReadSection<Background>(r, "backgrounds", cache);
        g.Paths = ReadSection<GamePath>(r, "paths", cache);
        g.Scripts = ReadSection<Script>(r, "scripts", cache);
        g.Fonts = ReadSection<Font>(r, "fonts", cache);
        g.Timelines = ReadSection<Timeline>(r, "timelines", cache);
        g.Objects = ReadSection<GameObject>(r, "objects", cache);
        g.Rooms = ReadSection<Room>(r, "rooms", cache);

        g.LastInstanceId = r.I32();
        g.LastTileId = r.I32();

        r.Expect(800, "included files");
        foreach (var raw in ReadBlobs(r, cache))
        {
            var br = new ByteReader(raw);
            g.IncludedFiles.Add(IncludedFile.Read(br));
            br.ExpectEnd("included file");
        }

        r.Expect(800, "game information");
        var info = ReadBlob(r, cache);
        var ir = new ByteReader(info);
        g.Information = GameInformation.Read(ir);
        ir.ExpectEnd("game information");

        r.Expect(500, "library init code");
        n = r.U32();
        for (var i = 0; i < n; i++) g.LibraryInit.Add(r.Str());

        r.Expect(700, "room order");
        n = r.U32();
        for (var i = 0; i < n; i++) g.RoomOrder.Add(r.I32());

        g.Trailer = r.Bytes(r.Remaining);
        return g;
    }

    public byte[] Write(BlobCache? cache = null)
    {
        var w = new ByteWriter(1 << 20);

        w.U32((uint)Garbage.Length);
        foreach (var x in Garbage) w.U32(x);
        w.Bool(Pro);
        w.U32(GameId);
        w.Bytes(Guid);

        w.U32(700);
        w.U32((uint)Extensions.Count);
        foreach (var e in Extensions) e.Write(w);

        WriteSection(w, Triggers, cache);

        w.U32(800);
        w.U32((uint)Constants.Count);
        foreach (var c in Constants)
        {
            w.Str(c.Name);
            w.Str(c.Value);
        }

        WriteSection(w, Sounds, cache);
        WriteSection(w, Sprites, cache);
        WriteSection(w, Backgrounds, cache);
        WriteSection(w, Paths, cache);
        WriteSection(w, Scripts, cache);
        WriteSection(w, Fonts, cache);
        WriteSection(w, Timelines, cache);
        WriteSection(w, Objects, cache);
        WriteSection(w, Rooms, cache);

        w.I32(LastInstanceId);
        w.I32(LastTileId);

        w.U32(800);
        WriteBlobs(w, IncludedFiles.Select(f => Encode(f.Write)).ToList(), cache);

        w.U32(800);
        w.Chunk(Compress(Encode(Information.Write), cache));

        w.U32(500);
        w.U32((uint)LibraryInit.Count);
        foreach (var s in LibraryInit) w.Str(s);

        w.U32(700);
        w.U32((uint)RoomOrder.Count);
        foreach (var i in RoomOrder) w.I32(i);

        w.Bytes(Trailer);
        return w.ToArray();
    }

    //---------------------------------------------------------------------------
    // Asset sections: [u32 800][u32 count]([u32 len][zlib])*, each blob
    // [u32 exists][body]. A deleted slot is a blob holding exists = 0 alone.
    //---------------------------------------------------------------------------

    private static List<T?> ReadSection<T>(ByteReader r, string what, BlobCache? cache) where T : class, IAsset<T>
    {
        r.Expect(800, what);
        var blobs = ReadBlobs(r, cache);
        var assets = new T?[blobs.Count];
        Parallelism.For(blobs.Count, i =>
        {
            var br = new ByteReader(blobs[i]);
            try
            {
                if (!br.Bool()) br.ExpectEnd("deleted asset");
                else
                {
                    assets[i] = T.Read(br);
                    br.ExpectEnd(typeof(T).Name);
                }
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"{what}[{i}]: {e.Message}", e);
            }
        });
        return [.. assets];
    }

    private static void WriteSection<T>(ByteWriter w, List<T?> assets, BlobCache? cache) where T : class, IAsset<T>
    {
        w.U32(800);
        WriteBlobs(w, assets.Select(a => Encode(bw =>
        {
            bw.Bool(a != null);
            a?.Write(bw);
        })).ToList(), cache);
    }

    private static List<byte[]> ReadBlobs(ByteReader r, BlobCache? cache)
    {
        var compressed = new byte[r.U32()][];
        for (var i = 0; i < compressed.Length; i++) compressed[i] = r.Chunk();
        var raw = new byte[compressed.Length][];
        Parallelism.For(compressed.Length, i =>
        {
            raw[i] = Zlib.Inflate(compressed[i]);
            cache?.Add(raw[i], compressed[i]);
        });
        return [.. raw];
    }

    private static byte[] ReadBlob(ByteReader r, BlobCache? cache)
    {
        var compressed = r.Chunk();
        var raw = Zlib.Inflate(compressed);
        cache?.Add(raw, compressed);
        return raw;
    }

    private static void WriteBlobs(ByteWriter w, List<byte[]> raw, BlobCache? cache)
    {
        var compressed = new byte[raw.Count][];
        Parallelism.For(raw.Count, i => compressed[i] = Compress(raw[i], cache));
        w.U32((uint)raw.Count);
        foreach (var c in compressed) w.Chunk(c);
    }

    private static byte[] Compress(byte[] raw, BlobCache? cache) => cache?.Deflate(raw) ?? Zlib.Deflate(raw);

    internal static byte[] Encode(System.Action<ByteWriter> write)
    {
        var w = new ByteWriter();
        write(w);
        return w.ToArray();
    }
}
