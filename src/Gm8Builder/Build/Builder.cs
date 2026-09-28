using Gm8Builder.Exe;
using Gm8Builder.Install;
using Gm8Builder.Pe;
using Gm8Builder.Tree;

namespace Gm8Builder.Build;

/// <summary>
/// Builds an executable from a split tree. The tree has the game; around it
/// go the runner, the DirectX DLL, the extension packages the game names and
/// the action libraries' init code. Those come from a Game Maker installation
/// (its data files - nothing is run), or failing that from a template: any
/// earlier build of the same game. The extension packages can instead come
/// from a directory of .gex files, so that a template older than the game
/// does not bring its older extensions with it.
/// </summary>
public static class Builder
{
    public sealed record Result(ExeFile Exe, IReadOnlyList<string> Warnings);

    /// <param name="gm8xFix">Apply gm8x_fix's patches to the runner, as projects commonly do after building with Game Maker.</param>
    /// <param name="gm8xFixSkip">Kinds of gm8x_fix patch to leave out (see <see cref="Gm8xFix.Kinds"/>).</param>
    /// <param name="extensions">A directory of .gex files to take the extension packages from, overriding the install or template.</param>
    public static Result Build(string tree, Gm8Install? install, ExeFile? template, int? seed = null, bool gm8xFix = false,
        IReadOnlyCollection<string>? gm8xFixSkip = null, string? extensions = null)
    {
        if (install == null && template == null) throw new ArgumentException("a build needs a Game Maker installation or a template");
        var project = TreeReader.Read(tree);
        var data = project.Data;
        var rng = seed is { } s ? new Random(s) : new Random();

        if (extensions != null)
        {
            data.Extensions = GexDirectory.Extensions(extensions, project.ExtensionNames, rng);
        }
        else if (install != null)
        {
            data.Extensions = install.Extensions(project.ExtensionNames, rng);
        }
        else
        {
            var have = template!.Data.Extensions.Select(e => e.Name).ToList();
            if (!project.ExtensionNames.SequenceEqual(have))
            {
                throw new InvalidDataException(
                    $"the tree uses extensions [{string.Join(", ", project.ExtensionNames)}] but the template has [{string.Join(", ", have)}]" +
                    " - pass --extensions to take them from .gex files instead");
            }
            data.Extensions = template.Data.Extensions;
        }
        data.LibraryInit = install?.LibraryInit() ?? template!.Data.LibraryInit;

        // Filler: the runner skips it, so any count and content will do.
        data.Garbage = RandomDwords(rng, rng.Next(100, 1000));
        data.Trailer = [];

        var runner = RunnerBuilder.Build(install?.Runner() ?? template!.Runner, project.Icon, project.Version);
        var warnings = project.Warnings.ToList();
        if (gm8xFix && Gm8xFix.Apply(runner, gm8xFixSkip).Count == 0) warnings.Add("gm8x_fix: no patch applied - the runner is already patched");

        var exe = new ExeFile
        {
            Runner = runner,
            Header3 = 0,
            Header4 = 800,
            Settings = project.Settings,
            DllName = "D3DX8.dll",
            Dll = install?.Dll() ?? template!.Dll,
            Garbage1 = RandomDwords(rng, rng.Next(100, 1000)),
            Garbage2 = RandomDwords(rng, rng.Next(100, 1000)),
            CipherTable = SwapCipher.RandomTable(rng),
            Data = data,
        };
        return new Result(exe, warnings);
    }

    private static uint[] RandomDwords(Random rng, int n)
    {
        var a = new uint[n];
        for (var i = 0; i < n; i++) a[i] = (uint)rng.NextInt64(0, 1L << 32);
        return a;
    }
}
