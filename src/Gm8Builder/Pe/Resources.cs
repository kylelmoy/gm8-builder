using System.Buffers.Binary;
using System.Text;

namespace Gm8Builder.Pe;

/// <summary>A node of a PE resource tree: a directory (Children) or a leaf (Data).</summary>
public sealed class ResourceNode
{
    public uint Id;
    /// <summary>Set for a named entry, which then ignores <see cref="Id"/>.</summary>
    public string? Name;
    public List<ResourceNode> Children = [];
    public byte[]? Data;
    public uint CodePage;
    public uint Characteristics;
    public uint TimeDateStamp;
    public ushort MajorVersion;
    public ushort MinorVersion;

    public bool IsDirectory => Data == null;

    public ResourceNode? Find(uint id) => Children.FirstOrDefault(c => c.Name == null && c.Id == id);
    public ResourceNode? Find(string name) => Children.FirstOrDefault(c => c.Name != null && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Keep entries in the order the loader binary-searches: names (case-insensitively), then ids.</summary>
    public void Sort()
    {
        Children = Children.Where(c => c.Name != null).OrderBy(c => c.Name!.ToUpperInvariant(), StringComparer.Ordinal)
            .Concat(Children.Where(c => c.Name == null).OrderBy(c => c.Id)).ToList();
        foreach (var c in Children) c.Sort();
    }
}

/// <summary>Reads and writes the .rsrc section of a PE file.</summary>
public static class ResourceSection
{
    public static ResourceNode Parse(ReadOnlySpan<byte> section, uint sectionVa)
    {
        var data = section.ToArray();
        return ParseDirectory(data, 0, sectionVa, 0);
    }

    private static ResourceNode ParseDirectory(byte[] s, int offset, uint va, int depth)
    {
        if (depth > 3) throw new InvalidDataException("resource tree is too deep");
        var dir = new ResourceNode
        {
            Characteristics = U32(s, offset),
            TimeDateStamp = U32(s, offset + 4),
            MajorVersion = U16(s, offset + 8),
            MinorVersion = U16(s, offset + 10),
        };
        var count = U16(s, offset + 12) + U16(s, offset + 14);
        for (var i = 0; i < count; i++)
        {
            var e = offset + 16 + i * 8;
            var name = U32(s, e);
            var target = U32(s, e + 4);
            ResourceNode child;
            if ((target & 0x80000000) != 0)
            {
                child = ParseDirectory(s, (int)(target & 0x7FFFFFFF), va, depth + 1);
            }
            else
            {
                var rva = U32(s, (int)target);
                var size = U32(s, (int)target + 4);
                child = new ResourceNode
                {
                    Data = s.AsSpan((int)(rva - va), (int)size).ToArray(),
                    CodePage = U32(s, (int)target + 8),
                };
            }
            if ((name & 0x80000000) != 0)
            {
                var at = (int)(name & 0x7FFFFFFF);
                child.Name = Encoding.Unicode.GetString(s, at + 2, U16(s, at) * 2);
            }
            else
            {
                child.Id = name;
            }
            dir.Children.Add(child);
        }
        return dir;
    }

    /// <summary>
    /// Lay the tree out at `sectionVa` as Game Maker's runner has it (the Delphi
    /// linker's layout): every directory table, then the data entries, then the
    /// name strings, then the data itself, 4-byte aligned.
    /// </summary>
    public static byte[] Write(ResourceNode root, uint sectionVa)
    {
        root.Sort();
        var dirs = new List<ResourceNode>();
        var leaves = new List<ResourceNode>();
        var names = new List<string>();
        var queue = new Queue<ResourceNode>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            dirs.Add(d);
            foreach (var c in d.Children)
            {
                if (c.Name != null && !names.Contains(c.Name)) names.Add(c.Name);
                if (c.IsDirectory) queue.Enqueue(c);
                else leaves.Add(c);
            }
        }

        var dirOffset = new Dictionary<ResourceNode, int>(ReferenceEqualityComparer.Instance);
        var at = 0;
        foreach (var d in dirs)
        {
            dirOffset[d] = at;
            at += 16 + d.Children.Count * 8;
        }
        var entryOffset = new Dictionary<ResourceNode, int>(ReferenceEqualityComparer.Instance);
        foreach (var l in leaves)
        {
            entryOffset[l] = at;
            at += 16;
        }
        var nameOffset = new Dictionary<string, int>();
        foreach (var n in names)
        {
            nameOffset[n] = at;
            at += 2 + n.Length * 2;
        }
        var dataOffset = new Dictionary<ResourceNode, int>(ReferenceEqualityComparer.Instance);
        foreach (var l in leaves)
        {
            at = Align(at, 4);
            dataOffset[l] = at;
            at += l.Data!.Length;
        }

        var s = new byte[Align(at, 4)];
        foreach (var d in dirs)
        {
            var o = dirOffset[d];
            W32(s, o, d.Characteristics);
            W32(s, o + 4, d.TimeDateStamp);
            W16(s, o + 8, d.MajorVersion);
            W16(s, o + 10, d.MinorVersion);
            W16(s, o + 12, (ushort)d.Children.Count(c => c.Name != null));
            W16(s, o + 14, (ushort)d.Children.Count(c => c.Name == null));
            for (var i = 0; i < d.Children.Count; i++)
            {
                var c = d.Children[i];
                var e = o + 16 + i * 8;
                W32(s, e, c.Name != null ? 0x80000000u | (uint)nameOffset[c.Name] : c.Id);
                W32(s, e + 4, c.IsDirectory ? 0x80000000u | (uint)dirOffset[c] : (uint)entryOffset[c]);
            }
        }
        foreach (var (n, o) in nameOffset)
        {
            W16(s, o, (ushort)n.Length);
            Encoding.Unicode.GetBytes(n).CopyTo(s, o + 2);
        }
        foreach (var l in leaves)
        {
            var o = entryOffset[l];
            W32(s, o, sectionVa + (uint)dataOffset[l]);
            W32(s, o + 4, (uint)l.Data!.Length);
            W32(s, o + 8, l.CodePage);
            l.Data.CopyTo(s, dataOffset[l]);
        }

        // Each alignment gap between data holds the start of "PADDINGXX", as
        // Windows' resource updater leaves it; the one before the first is zero.
        var end = leaves.Count > 0 ? dataOffset[leaves[0]] : s.Length;
        foreach (var l in leaves)
        {
            Fill(s, end, dataOffset[l], "PADDINGXX"u8);
            end = dataOffset[l] + l.Data!.Length;
        }
        Fill(s, end, s.Length, "PADDINGXX"u8);
        return s;
    }

    internal static int Align(int v, int a) => (v + a - 1) / a * a;

    /// <summary>Repeat `pattern` over [from, to).</summary>
    internal static void Fill(byte[] b, int from, int to, ReadOnlySpan<byte> pattern)
    {
        for (var i = from; i < to; i++) b[i] = pattern[(i - from) % pattern.Length];
    }
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    private static void W32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
    private static void W16(byte[] b, int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
}
