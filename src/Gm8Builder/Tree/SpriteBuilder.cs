using Gm8Builder.Images;
using Gm8Builder.Model;

namespace Gm8Builder.Tree;

// Names as gmksplit writes them, so Enum.Parse reads the XML directly.
public enum MaskShape { PRECISE, RECTANGLE, DISK, DIAMOND }
public enum BoundsMode { AUTO, FULL, MANUAL }

/// <summary>A sprite's settings from the tree: what the IDE turns images into masks with.</summary>
public sealed class SpriteDefinition
{
    public bool Separate;
    public MaskShape Shape;
    public BoundsMode BoundsMode;
    public int AlphaTolerance;
    public int Left, Right, Top, Bottom;
    public bool Transparent;
    public bool SmoothEdges;
}

/// <summary>
/// What Game Maker's IDE does to a sprite's images on the way into the
/// executable: converts the pixels and computes the collision masks the runner
/// uses as they are.
/// </summary>
public static class SpriteBuilder
{
    public static Sprite Build(string name, int originX, int originY, List<Rgba> images, SpriteDefinition def)
    {
        var s = new Sprite { Name = name, OriginX = originX, OriginY = originY, SeparateMasks = def.Separate };
        foreach (var img in images)
        {
            s.Frames.Add(new Frame
            {
                Width = (uint)img.Width,
                Height = (uint)img.Height,
                Pixels = Pixels(img, def.Transparent, def.SmoothEdges),
            });
        }
        if (images.Count == 0) return s;

        if (def.Separate)
        {
            foreach (var f in s.Frames) s.Masks.Add(Mask([f], def));
        }
        else
        {
            s.Masks.Add(Mask(s.Frames, def));
        }
        return s;
    }

    /// <summary>
    /// RGBA to the BGRA the runner uploads. On the way, each fully transparent
    /// pixel takes the colour of its first non-transparent neighbour, looking
    /// left, right, up, then down in the original image, so filtering at an edge
    /// does not pull in black. Its alpha stays 0; with no such neighbour it keeps
    /// its own colour. Verified on millions of transparent pixels in real builds.
    /// </summary>
    public static byte[] Pixels(Rgba img, bool transparent, bool smoothEdges)
    {
        if (transparent || smoothEdges)
            throw new NotSupportedException("the transparent and smooth edges image options are not supported yet");
        int w = img.Width, h = img.Height;
        var src = img.Pixels;
        var dst = new byte[src.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                var from = o;
                if (src[o + 3] == 0)
                {
                    if (x > 0 && src[o - 4 + 3] != 0) from = o - 4;
                    else if (x < w - 1 && src[o + 4 + 3] != 0) from = o + 4;
                    else if (y > 0 && src[o - w * 4 + 3] != 0) from = o - w * 4;
                    else if (y < h - 1 && src[o + w * 4 + 3] != 0) from = o + w * 4;
                }
                dst[o] = src[from + 2];
                dst[o + 1] = src[from + 1];
                dst[o + 2] = src[from];
                dst[o + 3] = src[o + 3];
            }
        }
        return dst;
    }

    private static CollisionMask Mask(List<Frame> frames, SpriteDefinition def)
    {
        var w = (int)frames.Max(f => f.Width);
        var h = (int)frames.Max(f => f.Height);

        // Solid where any frame's alpha exceeds the tolerance.
        var solid = new bool[w * h];
        foreach (var f in frames)
        {
            for (var y = 0; y < f.Height; y++)
            {
                for (var x = 0; x < f.Width; x++)
                {
                    if (f.Pixels[(y * (int)f.Width + x) * 4 + 3] > def.AlphaTolerance) solid[y * w + x] = true;
                }
            }
        }

        int left, right, top, bottom;
        switch (def.BoundsMode)
        {
            case BoundsMode.FULL:
                (left, right, top, bottom) = (0, w - 1, 0, h - 1);
                break;
            case BoundsMode.MANUAL:
                // Clamped to the image: projects can hold boxes past the edge.
                (left, right, top, bottom) = (Math.Clamp(def.Left, 0, w - 1), Math.Clamp(def.Right, 0, w - 1),
                    Math.Clamp(def.Top, 0, h - 1), Math.Clamp(def.Bottom, 0, h - 1));
                break;
            default:
                (left, right, top, bottom) = (w - 1, 0, h - 1, 0);
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        if (!solid[y * w + x]) continue;
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
                break;
        }

        var bits = new byte[w * h];
        for (var y = Math.Max(top, 0); y <= Math.Min(bottom, h - 1); y++)
        {
            for (var x = Math.Max(left, 0); x <= Math.Min(right, w - 1); x++)
            {
                bits[y * w + x] = def.Shape switch
                {
                    MaskShape.PRECISE => solid[y * w + x] ? (byte)1 : (byte)0,
                    MaskShape.RECTANGLE => 1,
                    _ => throw new NotSupportedException($"{def.Shape} collision masks are not supported yet"),
                };
            }
        }

        return new CollisionMask
        {
            Width = (uint)w,
            Height = (uint)h,
            Left = (uint)left,
            Right = (uint)right,
            Top = (uint)top,
            Bottom = (uint)bottom,
            Bits = bits,
        };
    }
}

/// <summary>
/// The loading image and progress bar images in the settings: a zlib'd 24-bit
/// BMP, bottom-up, alpha dropped. Header as Game Maker writes it: 54
/// bytes, no resolution, no palette.
/// </summary>
public static class SettingsImages
{
    public static byte[] Encode(Rgba img)
    {
        var stride = (img.Width * 3 + 3) & ~3;
        var size = stride * img.Height;
        var w = new IO.ByteWriter(54 + size);
        w.Bytes("BM"u8);
        w.U32((uint)(54 + size));
        w.U32(0);
        w.U32(54);
        w.U32(40);
        w.I32(img.Width);
        w.I32(img.Height);
        w.Bytes([1, 0, 24, 0]);
        w.U32(0); // BI_RGB
        w.U32((uint)size);
        w.U32(0);
        w.U32(0);
        w.U32(0);
        w.U32(0);
        var row = new byte[stride];
        for (var y = img.Height - 1; y >= 0; y--)
        {
            for (var x = 0; x < img.Width; x++)
            {
                var o = (y * img.Width + x) * 4;
                row[x * 3] = img.Pixels[o + 2];
                row[x * 3 + 1] = img.Pixels[o + 1];
                row[x * 3 + 2] = img.Pixels[o];
            }
            w.Bytes(row);
        }
        return IO.Zlib.Deflate(w.ToArray());
    }
}
