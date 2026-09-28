using Gm8Builder.Model;

namespace Gm8Builder.Install;

/// <summary>
/// Extension packages from a directory of .gex installers, the form a game's
/// repository usually keeps them in, instead of whatever versions a Game Maker
/// install or a template happens to carry.
///
/// Function ids are numbered as <see cref="Gm8Install.Extensions"/> numbers
/// them, as if exactly this directory's packages were the ones installed.
/// </summary>
public static class GexDirectory
{
    public static List<Extension> Extensions(string dir, IReadOnlyList<string> names, Random rng)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"no extensions directory {dir}");

        var packages = new Dictionary<string, ExtensionPackage>(StringComparer.Ordinal);
        foreach (var gex in Directory.EnumerateFiles(dir, "*.gex"))
        {
            var p = ExtensionPackage.ReadGex(gex);
            if (!packages.TryAdd(p.Name, p))
                throw new InvalidDataException($"{dir} has more than one .gex for the package \"{p.Name}\"");
        }

        uint next = 0;
        var built = new Dictionary<string, Extension>(StringComparer.Ordinal);
        foreach (var p in packages.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            built.Add(p.Name, p.ToExtension(ref next, rng));

        return names.Select(n => built.TryGetValue(n, out var e)
            ? e
            : throw new InvalidDataException($"extension package \"{n}\" has no .gex in {dir}")).ToList();
    }
}
