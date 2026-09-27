using Gm8Builder.Build;
using Gm8Builder.Exe;
using Gm8Builder.Install;
using Gm8Builder.IO;

namespace Gm8Builder.Tests;

/// <summary>
/// A tree built without Game Maker against Game Maker's own build of it.
/// Needs GM8_TEST_TREE (a split tree), GM8_TEST_EXE (Game Maker's build of
/// exactly that tree) and GM8_DIR (the installation that built it); passes
/// vacuously without them. To get a matching pair, split the .gmk that Game
/// Maker built the exe from with gmksplit.
/// </summary>
public class TreeBuildTests
{
    private static (string Tree, string Exe, string Gm8)? Inputs()
    {
        var tree = Environment.GetEnvironmentVariable("GM8_TEST_TREE");
        var exe = Environment.GetEnvironmentVariable("GM8_TEST_EXE");
        var gm8 = Environment.GetEnvironmentVariable("GM8_DIR");
        return string.IsNullOrEmpty(tree) || string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(gm8) ? null : (tree, exe, gm8);
    }

    [Fact]
    public void BuildMatchesGameMaker()
    {
        if (Inputs() is not var (tree, exePath, gm8)) return;
        var gm = ExeFile.Read(File.ReadAllBytes(exePath));
        var ours = ExeFile.Read(Builder.Build(tree, new Gm8Install(gm8), null, seed: 1).Exe.Write());

        // Filler is random by design.
        ours.Data.Garbage = gm.Data.Garbage;
        ours.Data.Trailer = gm.Data.Trailer;
        Assert.True(ours.Data.Write().AsSpan().SequenceEqual(gm.Data.Write()), "game data differs - run gm8-builder compare");

        // Settings, with images inflated: compression is ours.
        foreach (var s in new[] { ours.Settings, gm.Settings })
        {
            s.LoadingImage = s.LoadingImage is { } i ? Zlib.Inflate(i) : null;
            s.LoadingBarBack = s.LoadingBarBack is { } b ? Zlib.Inflate(b) : null;
            s.LoadingBarFront = s.LoadingBarFront is { } f ? Zlib.Inflate(f) : null;
        }
        Assert.Equal(GameDataBytes(gm.Settings.Write), GameDataBytes(ours.Settings.Write));
        Assert.Equal(gm.Dll, ours.Dll);
    }

    private static byte[] GameDataBytes(Action<ByteWriter> write)
    {
        var w = new ByteWriter();
        write(w);
        return w.ToArray();
    }
}
