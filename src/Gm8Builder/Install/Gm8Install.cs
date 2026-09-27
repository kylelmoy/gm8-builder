using Gm8Builder.IO;
using Gm8Builder.Model;

namespace Gm8Builder.Install;

/// <summary>
/// The parts of a Game Maker 8.0 installation a build needs. Nothing here runs
/// Game Maker - these are data files, and a copy of just them works on any
/// platform:
///
///   rundata                     [u32 len][zlib runner]
///   dxdata                      [u32 len][zlib D3DX8.dll], stored in games as is
///   lib/*.lib                   action libraries (for their init code)
///   extensions/*.ged + *.dat    installed extension packages
/// </summary>
public sealed class Gm8Install
{
    public string Directory { get; }

    public Gm8Install(string directory)
    {
        Directory = directory;
        if (!File.Exists(Path.Combine(directory, "rundata")))
            throw new FileNotFoundException($"{directory} does not look like a Game Maker 8 installation: it has no rundata");
    }

    /// <summary>The runner as shipped, before the IDE sets its icon and version information.</summary>
    public byte[] Runner() => Zlib.Inflate(File.ReadAllBytes(Path.Combine(Directory, "rundata")).AsSpan(4));

    /// <summary>D3DX8.dll, compressed as games carry it.</summary>
    public byte[] Dll() => File.ReadAllBytes(Path.Combine(Directory, "dxdata")).AsSpan(4).ToArray();

    /// <summary>
    /// Each action library's init code, in file-name order - one string per
    /// library, used or not.
    /// </summary>
    public List<string> LibraryInit()
    {
        var dir = Path.Combine(Directory, "lib");
        if (!System.IO.Directory.Exists(dir)) return [];
        return System.IO.Directory.EnumerateFiles(dir, "*.lib")
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(ReadLibraryInit)
            .ToList();
    }

    /// <summary>An action library: version, tab caption, id, author, version, date, info, then the init code.</summary>
    private static string ReadLibraryInit(string path)
    {
        var r = new ByteReader(File.ReadAllBytes(path));
        var version = r.U32();
        if (version != 500 && version != 520) throw new InvalidDataException($"{path}: library version {version}");
        r.Str();
        r.U32();
        r.Str();
        r.U32();
        r.F64();
        r.Str();
        return r.Str();
    }

    /// <summary>
    /// The named packages as a game embeds them. Game Maker numbers extension
    /// functions through every installed package in name order, used or not, so
    /// ids here match a real build only against the same set of installed packages.
    /// </summary>
    public List<Extension> Extensions(IReadOnlyList<string> names, Random rng)
    {
        var dir = Path.Combine(Directory, "extensions");
        var installed = System.IO.Directory.Exists(dir)
            ? System.IO.Directory.EnumerateFiles(dir, "*.ged").Order(StringComparer.OrdinalIgnoreCase)
                .Select(ged => ExtensionPackage.ReadInstalled(ged, Path.ChangeExtension(ged, ".dat"))).ToList()
            : [];

        uint next = 0;
        var built = new Dictionary<string, Extension>(StringComparer.Ordinal);
        foreach (var p in installed)
        {
            var e = p.ToExtension(ref next, rng);
            built.TryAdd(p.Name, e);
        }
        return names.Select(n => built.TryGetValue(n, out var e)
            ? e
            : throw new InvalidDataException($"extension package \"{n}\" is not installed in {dir}")).ToList();
    }
}
