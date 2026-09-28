using Gm8Builder.Exe;
using Gm8Builder.Install;
using Gm8Builder.IO;
using Gm8Builder.Model;

namespace Gm8Builder.Tests;

public class GexDirectoryTests
{
    /// <summary>A .gex holding one DLL file, with the given functions, and the given contents.</summary>
    private static byte[] Gex(string name, string file, byte[] contents, params string[] functions)
    {
        var w = new ByteWriter();
        w.U32(700);
        w.U32(0); // editable
        w.Str(name);
        w.Str("temp1");
        w.Str("1.0");
        foreach (var s in new[] { "author", "date", "licence", "description", "help" }) w.Str(s);
        w.U32(0); // hidden
        w.U32(0); // uses
        w.U32(1); // files
        w.U32(700);
        w.Str(file);
        w.Str(@"C:\" + file);
        w.U32(1); // DLL
        w.Str("");
        w.Str("");
        w.U32((uint)functions.Length);
        foreach (var f in functions)
        {
            w.U32(700);
            w.Str(f);
            w.Str(f + "_external");
            w.U32(11);
            w.Str(""); // help line
            w.U32(0); // hidden
            w.I32(1);
            for (var k = 0; k < ExtensionFunction.MaxArgs; k++) w.U32(2);
            w.U32(2);
        }
        w.U32(0); // constants
        w.Chunk(Zlib.Deflate(contents));

        const uint seed = 1234;
        var head = new ByteWriter();
        head.U32(1234321);
        head.U32(701);
        head.U32(seed);
        head.Bytes(ExtensionCipher.Encrypt(w.ToArray(), seed));
        return head.ToArray();
    }

    [Fact]
    public void TakesNamedPackagesAndNumbersFunctionsAcrossTheDirectory()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.Combine("b.gex"), Gex("Beta", "beta.dll", [1, 2, 3], "beta_one", "beta_two"));
        File.WriteAllBytes(dir.Combine("a.gex"), Gex("Alpha", "alpha.dll", [9], "alpha_one"));

        var built = GexDirectory.Extensions(dir.Path, ["Beta"], new Random(1));

        var beta = Assert.Single(built);
        Assert.Equal("Beta", beta.Name);
        // Alpha is not used, but installed packages are numbered in name order all the same.
        Assert.Equal([1u, 2u], beta.Files[0].Functions.Select(f => f.Id));
        Assert.Equal("beta_two_external", beta.Files[0].Functions[1].ExternalName);
        Assert.Equal(new byte[] { 1, 2, 3 }, Zlib.Inflate(new ByteReader(beta.Payload).Chunk()));
    }

    [Fact]
    public void RefusesAPackageItDoesNotHave()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.Combine("a.gex"), Gex("Alpha", "alpha.dll", [9], "alpha_one"));
        var e = Assert.Throws<InvalidDataException>(() => GexDirectory.Extensions(dir.Path, ["Beta"], new Random(1)));
        Assert.Contains("\"Beta\"", e.Message);
    }

    [Fact]
    public void RefusesTwoVersionsOfOnePackage()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.Combine("a1.gex"), Gex("Alpha", "alpha.dll", [9], "alpha_one"));
        File.WriteAllBytes(dir.Combine("a2.gex"), Gex("Alpha", "alpha.dll", [8], "alpha_one"));
        Assert.Throws<InvalidDataException>(() => GexDirectory.Extensions(dir.Path, ["Alpha"], new Random(1)));
    }
}
