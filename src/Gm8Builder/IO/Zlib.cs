using System.IO.Compression;

namespace Gm8Builder.IO;

/// <summary>
/// Every compressed thing in a GM8 executable is a plain zlib stream. The runner
/// only inflates, so our deflate need not match Delphi's byte for byte.
/// </summary>
public static class Zlib
{
    public static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(data.Length * 4);
        z.CopyTo(output);
        return output.ToArray();
    }

    public static byte[] Deflate(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream(data.Length / 2 + 64);
        using (var z = new ZLibStream(output, level, leaveOpen: true)) z.Write(data);
        return output.ToArray();
    }
}
