using System.Buffers.Binary;

namespace Gm8Builder.IO;

/// <summary>The writing half of <see cref="ByteReader"/>.</summary>
public sealed class ByteWriter(int capacity = 256)
{
    private byte[] _buf = new byte[Math.Max(capacity, 16)];

    public int Length { get; private set; }

    private Span<byte> Grow(int n)
    {
        if (Length + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + n));
        var span = _buf.AsSpan(Length, n);
        Length += n;
        return span;
    }

    public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), v);
    public void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Grow(4), v);
    public void F64(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Grow(8), v);
    public void Bool(bool v) => U32(v ? 1u : 0u);
    public void Bytes(ReadOnlySpan<byte> v) => v.CopyTo(Grow(v.Length));

    public void Chunk(ReadOnlySpan<byte> v)
    {
        U32((uint)v.Length);
        Bytes(v);
    }

    public void Str(string s) => Chunk(Latin1.Encode(s));

    /// <summary>Overwrite a u32 already written, for lengths only known afterwards.</summary>
    public void PatchU32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(at, 4), v);

    public byte[] ToArray() => _buf.AsSpan(0, Length).ToArray();
}
