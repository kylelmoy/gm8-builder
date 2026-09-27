using System.Buffers.Binary;
using Gm8Builder.IO;
using Gm8Builder.Model;

namespace Gm8Builder.Exe;

/// <summary>
/// A Game Maker 8.0 executable, taken apart.
///
///   [runner ........................][header @ ptr(0x144AC0)][encrypted stream][tail]
///
/// The runner is YoYo's program; the GM8.0 runner reads its gamedata offset from
/// a dword at 0x144AC0 (normally 2,000,000) and finds, there:
///
///   u32 1234321, u32 800, u32 ?, u32 ?
///   [len][zlib settings]
///   [len][dll name] [len][dll]                 (D3DX8.dll, extracted at start)
///   u32 n1, u32 n2, n1 dwords, 256-byte table, n2 dwords
///   [len][stream encrypted with the table]     (see GameData)
///
/// Nothing holds an absolute offset past the header, so any section can change
/// size.
/// </summary>
public sealed class ExeFile
{
    public const int HeaderPointer = 0x144AC0;
    public const uint Magic = 1234321;
    public const uint Version = 800;

    /// <summary>Everything before the gamedata header: runner, PE resources, icon, gm8x_fix's patches.</summary>
    public byte[] Runner = [];
    /// <summary>The two dwords after magic and version. Meaning unknown; carried through.</summary>
    public uint Header3;
    public uint Header4;
    public Settings Settings = new();
    public string DllName = "D3DX8.dll";
    public byte[] Dll = [];
    public uint[] Garbage1 = [];
    public uint[] Garbage2 = [];
    public byte[] CipherTable = new byte[256];
    public GameData Data = new();
    /// <summary>Anything after the stream. Empty in a plain build.</summary>
    public byte[] Tail = [];

    public static ExeFile Read(byte[] exe, BlobCache? cache = null)
    {
        if (exe.Length < HeaderPointer + 4) throw new InvalidDataException("not a GM8.0 executable: too short");
        var headerPos = (int)BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(HeaderPointer));
        if (headerPos <= HeaderPointer || headerPos >= exe.Length)
            throw new InvalidDataException($"gamedata pointer 0x{headerPos:x} out of range");

        var r = new ByteReader(exe, headerPos);
        if (r.U32() != Magic) throw new InvalidDataException($"no GM8.0 gamedata at 0x{headerPos:x}");
        r.Expect(Version, "gamedata header (only GM8.0 is supported)");

        var f = new ExeFile { Runner = exe.AsSpan(0, headerPos).ToArray(), Header3 = r.U32(), Header4 = r.U32() };

        var settingsZ = r.Chunk();
        var settingsRaw = Zlib.Inflate(settingsZ);
        cache?.Add(settingsRaw, settingsZ);
        var sr = new ByteReader(settingsRaw);
        f.Settings = Settings.Read(sr);

        f.DllName = r.Str();
        f.Dll = r.Chunk();

        f.Garbage1 = new uint[r.U32()];
        f.Garbage2 = new uint[r.U32()];
        for (var i = 0; i < f.Garbage1.Length; i++) f.Garbage1[i] = r.U32();
        f.CipherTable = r.Bytes(256);
        for (var i = 0; i < f.Garbage2.Length; i++) f.Garbage2[i] = r.U32();

        var stream = SwapCipher.Decrypt(r.Chunk(), f.CipherTable);
        f.Data = GameData.Read(stream, cache);
        f.Tail = r.Bytes(r.Remaining);
        return f;
    }

    public byte[] Write(BlobCache? cache = null)
    {
        // The header must land where the runner looks for it.
        var pointer = BinaryPrimitives.ReadUInt32LittleEndian(Runner.AsSpan(HeaderPointer));
        if (pointer != Runner.Length)
            throw new InvalidOperationException($"runner looks for its gamedata at 0x{pointer:x}, but is 0x{Runner.Length:x} bytes long");

        var w = new ByteWriter(Runner.Length + (16 << 20));
        w.Bytes(Runner);

        w.U32(Magic);
        w.U32(Version);
        w.U32(Header3);
        w.U32(Header4);

        var settings = GameData.Encode(Settings.Write);
        w.Chunk(cache?.Deflate(settings) ?? Zlib.Deflate(settings));
        w.Str(DllName);
        w.Chunk(Dll);

        w.U32((uint)Garbage1.Length);
        w.U32((uint)Garbage2.Length);
        foreach (var x in Garbage1) w.U32(x);
        w.Bytes(CipherTable);
        foreach (var x in Garbage2) w.U32(x);
        w.Chunk(SwapCipher.Encrypt(Data.Write(cache), CipherTable));

        w.Bytes(Tail);
        return w.ToArray();
    }
}
