using System.Buffers.Binary;
using Gm8Builder.IO;

namespace Gm8Builder.Images;

/// <summary>An image as 8-bit RGBA, row-major, top row first.</summary>
public sealed class Rgba(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;
}

/// <summary>
/// A PNG decoder, just enough for a project's images: every colour type and
/// bit depth, tRNS, and Adam7. No colour management - gAMA and friends are
/// ignored, as Java's ImageIO (which gmksplit reads with) ignores them.
/// </summary>
public static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static Rgba Read(string path)
    {
        try
        {
            return Decode(File.ReadAllBytes(path));
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    public static Rgba Decode(byte[] png)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("not a PNG");

        int width = 0, height = 0, depth = 0, colour = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? paletteAlpha = null;
        int[]? transparent = null; // grey or RGB sample values that are fully transparent
        using var idat = new MemoryStream();

        var at = 8;
        while (at + 8 <= png.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            if (len < 0 || at + 12L + len > png.Length) throw new InvalidDataException($"truncated {type} chunk");
            var data = png.AsSpan(at + 8, len);
            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    depth = data[8];
                    colour = data[9];
                    interlace = data[12];
                    break;
                case "PLTE":
                    palette = data.ToArray();
                    break;
                case "tRNS":
                    if (colour == 3) paletteAlpha = data.ToArray();
                    else if (colour == 0) transparent = [BinaryPrimitives.ReadUInt16BigEndian(data)];
                    else if (colour == 2)
                        transparent = [BinaryPrimitives.ReadUInt16BigEndian(data), BinaryPrimitives.ReadUInt16BigEndian(data[2..]), BinaryPrimitives.ReadUInt16BigEndian(data[4..])];
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    at = png.Length;
                    continue;
            }
            at += 12 + len;
        }

        if (width <= 0 || height <= 0) throw new InvalidDataException("no image header");
        var channels = colour switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException($"colour type {colour}"),
        };
        if (colour == 3 && palette == null) throw new InvalidDataException("palette image without a palette");

        var raw = Zlib.Inflate(idat.ToArray());
        var bitsPerPixel = channels * depth;
        var output = new byte[width * height * 4];
        var ctx = new Context(width, depth, colour, channels, palette, paletteAlpha, transparent, output);

        if (interlace == 0)
        {
            Unfilter(raw, 0, width, height, bitsPerPixel, (x, y, row, i) => ctx.Put(x, y, row, i));
        }
        else
        {
            ReadOnlySpan<(int X0, int Y0, int Dx, int Dy)> passes = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
            var offset = 0;
            foreach (var (x0, y0, dx, dy) in passes)
            {
                var pw = (width - x0 + dx - 1) / dx;
                var ph = (height - y0 + dy - 1) / dy;
                if (pw <= 0 || ph <= 0) continue;
                offset = Unfilter(raw, offset, pw, ph, bitsPerPixel, (x, y, row, i) => ctx.Put(x0 + x * dx, y0 + y * dy, row, i));
            }
        }
        return new Rgba(width, height, output);
    }

    private sealed class Context(int width, int depth, int colour, int channels, byte[]? palette, byte[]? paletteAlpha, int[]? transparent, byte[] output)
    {
        private int Sample(byte[] row, int pixel, int channel)
        {
            if (depth == 8) return row[pixel * channels + channel];
            if (depth == 16) return (row[(pixel * channels + channel) * 2] << 8) | row[(pixel * channels + channel) * 2 + 1];
            var bit = pixel * depth; // sub-byte depths only occur with one channel
            return (row[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
        }

        private byte To8(int v) => depth switch
        {
            8 => (byte)v,
            16 => (byte)(v >> 8),
            _ => (byte)(v * 255 / ((1 << depth) - 1)),
        };

        public void Put(int x, int y, byte[] row, int i)
        {
            var o = (y * width + x) * 4;
            switch (colour)
            {
                case 0:
                {
                    var g = Sample(row, i, 0);
                    output[o] = output[o + 1] = output[o + 2] = To8(g);
                    output[o + 3] = transparent != null && transparent[0] == g ? (byte)0 : (byte)255;
                    break;
                }
                case 2:
                {
                    int r = Sample(row, i, 0), g = Sample(row, i, 1), b = Sample(row, i, 2);
                    output[o] = To8(r);
                    output[o + 1] = To8(g);
                    output[o + 2] = To8(b);
                    output[o + 3] = transparent != null && transparent[0] == r && transparent[1] == g && transparent[2] == b ? (byte)0 : (byte)255;
                    break;
                }
                case 3:
                {
                    var p = Sample(row, i, 0);
                    if (p * 3 + 2 >= palette!.Length) throw new InvalidDataException($"palette index {p} out of range");
                    output[o] = palette[p * 3];
                    output[o + 1] = palette[p * 3 + 1];
                    output[o + 2] = palette[p * 3 + 2];
                    output[o + 3] = paletteAlpha != null && p < paletteAlpha.Length ? paletteAlpha[p] : (byte)255;
                    break;
                }
                case 4:
                    output[o] = output[o + 1] = output[o + 2] = To8(Sample(row, i, 0));
                    output[o + 3] = To8(Sample(row, i, 1));
                    break;
                case 6:
                    output[o] = To8(Sample(row, i, 0));
                    output[o + 1] = To8(Sample(row, i, 1));
                    output[o + 2] = To8(Sample(row, i, 2));
                    output[o + 3] = To8(Sample(row, i, 3));
                    break;
            }
        }
    }

    /// <summary>Reverse the per-row filters of one (sub)image starting at `offset`; returns where it ends.</summary>
    private static int Unfilter(byte[] raw, int offset, int width, int height, int bitsPerPixel, Action<int, int, byte[], int> put)
    {
        var stride = (width * bitsPerPixel + 7) / 8;
        var bpp = Math.Max(1, bitsPerPixel / 8);
        var prev = new byte[stride];
        var row = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            if (offset + 1 + stride > raw.Length) throw new InvalidDataException("image data is short");
            var filter = raw[offset];
            raw.AsSpan(offset + 1, stride).CopyTo(row);
            offset += 1 + stride;
            for (var i = 0; i < stride; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                row[i] = filter switch
                {
                    0 => row[i],
                    1 => (byte)(row[i] + a),
                    2 => (byte)(row[i] + b),
                    3 => (byte)(row[i] + ((a + b) >> 1)),
                    4 => (byte)(row[i] + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"filter type {filter}"),
                };
            }
            for (var x = 0; x < width; x++) put(x, y, row, x);
            (prev, row) = (row, prev);
        }
        return offset;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
