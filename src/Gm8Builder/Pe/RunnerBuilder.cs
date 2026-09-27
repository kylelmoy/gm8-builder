using System.Buffers.Binary;
using System.Text;

namespace Gm8Builder.Pe;

/// <summary>What the IDE writes into the runner's version resource, from the game's settings.</summary>
public sealed record VersionInfo(
    int Major, int Minor, int Release, int Build,
    string Company, string Product, string Copyright, string Description);

/// <summary>
/// Turns Game Maker's stock runner (rundata) into the one a game ships with -
/// the only two things the IDE changes in it:
///
/// - the icon: MAINICON and its images are replaced by the game's .ico, under
///   language 1033;
/// - the version resource, a template of fixed-width placeholder slots that
///   is filled in place (so its size never changes).
///
/// The resource section is the runner's last, so rewriting it only moves the
/// end of the file, and the result is padded to where the gamedata starts.
/// </summary>
public static class RunnerBuilder
{
    public const int GamedataOffset = 2_000_000;

    private const uint RtIcon = 3;
    private const uint RtGroupIcon = 14;
    private const uint RtVersion = 16;
    private const uint LangEnglishUs = 1033;

    public static byte[] Build(byte[] runner, byte[]? ico, VersionInfo? version)
    {
        var pe = (int)U32(runner, 0x3C);
        if (U32(runner, pe) != 0x4550) throw new InvalidDataException("runner is not a PE file");
        var sections = U16(runner, pe + 6);
        var optional = pe + 24;
        var sectionTable = optional + U16(runner, pe + 20);
        var fileAlign = (int)U32(runner, optional + 36);
        var sectionAlign = (int)U32(runner, optional + 32);

        int rsrc = -1;
        for (var i = 0; i < sections; i++)
        {
            if (Encoding.ASCII.GetString(runner, sectionTable + i * 40, 5) == ".rsrc") rsrc = sectionTable + i * 40;
        }
        if (rsrc < 0) throw new InvalidDataException("runner has no resource section");
        var va = U32(runner, rsrc + 12);
        var rawSize = (int)U32(runner, rsrc + 16);
        var rawPtr = (int)U32(runner, rsrc + 20);
        // A runner taken from a built game is already padded to the gamedata; only the image counts.
        if (rawPtr + rawSize > runner.Length || rsrc != sectionTable + (sections - 1) * 40)
            throw new InvalidDataException("the runner's resource section is not its last - this is not a GM8.0 runner");

        var root = ResourceSection.Parse(runner.AsSpan(rawPtr, rawSize), va);
        if (ico != null) SetIcon(root, ico);
        if (version != null) SetVersion(root, version);

        Restamp(root);
        var section = ResourceSection.Write(root, va);
        var newRaw = ResourceSection.Align(section.Length, fileAlign);
        var output = new byte[GamedataOffset];
        if (rawPtr + newRaw > GamedataOffset)
            throw new InvalidDataException($"the runner with this icon is {rawPtr + newRaw:N0} bytes, past where the game data must start");
        runner.AsSpan(0, rawPtr).CopyTo(output);
        section.CopyTo(output, rawPtr);
        ResourceSection.Fill(output, rawPtr + section.Length, rawPtr + newRaw, "PADDINGXXPADDING"u8);

        // Section header, then the optional header's totals and resource directory.
        var oldVirtual = U32(runner, rsrc + 8);
        W32(output, rsrc + 8, (uint)section.Length);
        W32(output, rsrc + 16, (uint)newRaw);
        W32(output, optional + 8, (uint)(U32(runner, optional + 8) - rawSize + newRaw)); // SizeOfInitializedData
        W32(output, optional + 56, (uint)(U32(runner, optional + 56)
            - ResourceSection.Align((int)oldVirtual, sectionAlign) + ResourceSection.Align(section.Length, sectionAlign))); // SizeOfImage
        W32(output, optional + 96 + 2 * 8 + 4, (uint)section.Length);
        return output;
    }

    /// <summary>The IDE's rewrite leaves every directory with no timestamp and version 4.0, and every entry in code page 1252.</summary>
    private static void Restamp(ResourceNode dir)
    {
        if (!dir.IsDirectory)
        {
            dir.CodePage = 1252;
            return;
        }
        dir.TimeDateStamp = 0;
        dir.MajorVersion = 4;
        dir.MinorVersion = 0;
        foreach (var c in dir.Children) Restamp(c);
    }

    /// <summary>ICONDIR { u16 0, u16 1, u16 n } then n x { w, h, colours, 0, u16 planes, u16 bpp, u32 size, u32 offset }.</summary>
    private static void SetIcon(ResourceNode root, byte[] ico)
    {
        if (ico.Length < 6 || U16(ico, 2) != 1) throw new InvalidDataException("the game icon is not an .ico file");
        var count = U16(ico, 4);

        root.Children.RemoveAll(c => c.Name == null && c.Id == RtIcon);
        root.Find(RtGroupIcon)?.Children.RemoveAll(c => string.Equals(c.Name, "MAINICON", StringComparison.OrdinalIgnoreCase));

        var icons = new ResourceNode { Id = RtIcon };
        var group = new byte[6 + count * 14];
        W16(group, 2, 1);
        W16(group, 4, count);
        for (var i = 0; i < count; i++)
        {
            var e = 6 + i * 16;
            var size = (int)U32(ico, e + 8);
            var offset = (int)U32(ico, e + 12);
            if (offset + size > ico.Length) throw new InvalidDataException("the game icon is truncated");
            ico.AsSpan(e, 12).CopyTo(group.AsSpan(6 + i * 14));
            W16(group, 6 + i * 14 + 12, (ushort)(i + 1));
            icons.Children.Add(Leaf((uint)(i + 1), ico.AsSpan(offset, size).ToArray()));
        }
        root.Children.Add(icons);

        var groups = root.Find(RtGroupIcon);
        if (groups == null) root.Children.Add(groups = new ResourceNode { Id = RtGroupIcon });
        groups.Children.Add(new ResourceNode { Name = "MAINICON", Children = [new ResourceNode { Id = LangEnglishUs, Data = group }] });
    }

    private static ResourceNode Leaf(uint id, byte[] data) =>
        new() { Id = id, Children = [new ResourceNode { Id = LangEnglishUs, Data = data }] };

    private static void SetVersion(ResourceNode root, VersionInfo v)
    {
        var leaf = root.Find(RtVersion)?.Children.FirstOrDefault()?.Children.FirstOrDefault()
            ?? throw new InvalidDataException("the runner has no version resource");
        var b = leaf.Data!;

        // VS_FIXEDFILEINFO follows the 38-byte header and key; the versions are at +8.
        uint ms = (uint)((v.Major << 16) | (v.Minor & 0xFFFF)), ls = (uint)((v.Release << 16) | (v.Build & 0xFFFF));
        W32(b, 0x30, ms);
        W32(b, 0x34, ls);
        W32(b, 0x38, ms);
        W32(b, 0x3C, ls);

        var number = $"{v.Major}.{v.Minor}.{v.Release}.{v.Build}";
        Fill(b, "CompanyName", v.Company);
        Fill(b, "FileDescription", v.Description);
        Fill(b, "FileVersion", number);
        Fill(b, "LegalCopyright", v.Copyright);
        Fill(b, "ProductName", v.Product);
        Fill(b, "ProductVersion", number);
    }

    /// <summary>Overwrite a String's value slot in place, zero-filled; too long a value is cut to fit with its terminator.</summary>
    private static void Fill(byte[] b, string key, string value)
    {
        var k = Encoding.Unicode.GetBytes(key + "\0");
        var i = b.AsSpan().IndexOf(k);
        if (i < 6) throw new InvalidDataException($"the runner's version resource has no {key}");
        var start = i - 6;
        var end = start + U16(b, start);
        var slot = ((i + k.Length + 3) & ~3);
        var room = (end - slot) / 2 - 1;
        var text = Encoding.Unicode.GetBytes(value.Length > room ? value[..room] : value);
        b.AsSpan(slot, end - slot).Clear();
        text.CopyTo(b, slot);
    }

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    private static void W32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
    private static void W16(byte[] b, int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
}
