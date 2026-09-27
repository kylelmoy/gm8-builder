using System.Buffers.Binary;

namespace Gm8Builder.IO;

/// <summary>
/// Little-endian reads over a byte array, the only way GM8 lays anything out.
/// Every read is bounds-checked and fails with the offset, since a wrong guess
/// about the format shows up as a read running off the end.
/// </summary>
public sealed class ByteReader(byte[] data, int start = 0, int? end = null)
{
    public byte[] Data { get; } = data;
    public int Position { get; set; } = start;
    public int End { get; } = end ?? data.Length;
    public int Remaining => End - Position;

    private int Take(int n)
    {
        if (n < 0 || Position + n > End)
            throw new InvalidDataException($"read of {n} bytes at 0x{Position:x} runs past the end (0x{End:x})");
        var at = Position;
        Position += n;
        return at;
    }

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(Take(4), 4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(Take(4), 4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Data.AsSpan(Take(8), 8));

    /// <summary>A u32 that GM8 only ever writes as 0 or 1. Anything else is a misread.</summary>
    public bool Bool()
    {
        var at = Position;
        var v = U32();
        return v switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException($"expected a boolean at 0x{at:x}, found {v}"),
        };
    }

    public byte[] Bytes(int n) => Data.AsSpan(Take(n), n).ToArray();

    /// <summary>[u32 length][bytes].</summary>
    public byte[] Chunk() => Bytes(checked((int)U32()));

    /// <summary>[u32 length][bytes] as a byte-per-char string - see <see cref="Latin1"/>.</summary>
    public string Str()
    {
        var n = checked((int)U32());
        return Latin1.Decode(Data.AsSpan(Take(n), n));
    }

    /// <summary>A version field that must hold one value; anything else means we are lost.</summary>
    public void Expect(uint version, string what)
    {
        var at = Position;
        var v = U32();
        if (v != version) throw new InvalidDataException($"{what}: expected version {version} at 0x{at:x}, found {v}");
    }

    /// <summary>The next bytes as hex, for error messages.</summary>
    public string Peek(int n) => Convert.ToHexString(Data.AsSpan(Position, Math.Min(n, End - Position)));

    public void ExpectEnd(string what)
    {
        if (Position != End)
            throw new InvalidDataException($"{what}: {End - Position} unread bytes at 0x{Position:x}, starting {Peek(32)}");
    }
}
